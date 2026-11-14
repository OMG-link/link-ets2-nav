using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationRuntimeTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (NearbyMapSnapshot Map,RouteSnapshot Route) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,new(0,0,0),new(0,0,700),default);
        var right=Road.Add(map,new(-30,0,730),new(-600,0,730),default);
        var straight=Road.Add(map,new(0,0,760),new(0,0,1300),default);
        DirectedRoad[] links=[
            new("prefab:ABC:0:1",incoming.ForwardNode.Uid,right.Node.Uid,
                [new(0,0,700),new(0,0,710),new(-10,0,725),new(-30,0,730)]) { Junction = new(2, 2, 1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=-Vector3.UnitX },
            new("prefab:ABC:0:2",incoming.ForwardNode.Uid,straight.Node.Uid,[new(0,0,700),new(0,0,760)]) { Junction = new(2, 2, 1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ }];
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,1300,60),
            new(incoming.ForwardNode.Uid,600,30),new(right.Node.Uid,570,28),new(right.ForwardNode.Uid,0,0)],Now);
        return(new(map,[],[],[]){PrefabPaths=links},route);
    }
    private static GameDataSnapshot Data(RouteSnapshot route,float z,RouteAvailability availability=RouteAvailability.Available,
        bool paused=false,bool stale=false)=>new(
        new(0,0,z,.5,20,1000,true,paused,1,stale?Now.AddSeconds(-3):Now),
        route,availability,"session",!paused);
    private static NavigationRuntime ObserveDiagnostics(Action cue,Action<string>? log=null)=>new(log:line=>
    {
        if(line.StartsWith("\u5bfc\u822a\u6307\u4ee4\uff1a",StringComparison.Ordinal))cue();
        log?.Invoke(line);
    });

    [Fact]
    public void NarrowRoadUsesSharedEnvironmentPipelineBeforeItsEntrance()
    {
        var scene=Scene();var right=scene.Map.Map.MapItems.Values.OfType<Road>().Single(r=>r.Node.Position.X==-30);
        var geometry=new DirectedRoad(right.Uid.ToString("X16"),right.Node.Uid,right.ForwardNode.Uid,[right.Node.Position,right.ForwardNode.Position]);
        var nearby=scene.Map with{SpeedLimits=new(new Dictionary<ulong,LimitRoad>{{right.Uid,new(geometry,"local_road"){SingleTrack=true}}},[],[],new Dictionary<string,TruckSpeedLimit>(),null)};
        var guidance=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();guidance.Queued+=queued.Add;
        var runtime=new NavigationRuntime(guidance:guidance);
        foreach(float z in new[]{0f,300f,600f})
        {
            var data=Data(scene.Route,z);runtime.Process(data,nearby,Now);guidance.Tick(data,Now);
        }
        Assert.Equal(new[]{"五百米后，进入窄路，请减速观察。","两百米后，进入窄路，请减速观察。"},
            queued.Where(q=>q.Source==NarrowRoadEventBuilder.Kind).Select(q=>q.Text));
    }

    [Fact]
    public async Task CityEntryRunsOnItsOwnEnvironmentChainAndClearsAfterPassingTheUrbanSign()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(0,0,1000),default);
        var route=RouteSnapshot.Parse([new(road.Node.Uid,1000,50),new(road.ForwardNode.Uid,100,5)],Now);
        var geometry=new DirectedRoad(road.Uid.ToString("X16"),road.Node.Uid,road.ForwardNode.Uid,[new(0,0,0),new(0,0,1000)]);
        var limits=new SpeedLimitData(new Dictionary<ulong,LimitRoad>{{road.Uid,new(geometry,"local_road")}},
            [new("city-start",new(2,0,600),Vector3.UnitZ,false,new(Urban:true))],[],new Dictionary<string,TruckSpeedLimit>(),null);
        var scene=new NearbyMapSnapshot(map,[],[],[]){SpeedLimits=ProjectedSignBuilder.Build(limits,[],TestContext.Current.CancellationToken)};
        await using var playback=new GuidancePlaybackManager(null,new CallbackPlayer(_=>{}),()=>null);
        var coordinator=new GuidanceCoordinator(playback:playback);var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        var runtime=new NavigationRuntime(guidance:coordinator);
        void Step(float z){var data=Data(route,z);runtime.Process(data,scene,Now);coordinator.Tick(data,Now);}
        Step(0);Assert.DoesNotContain(queued,q=>q.Source==CityEntryEventBuilder.Kind);
        Step(100);Assert.Equal("五百米后，进入城区，请注意减速慢行。",Assert.Single(queued,q=>q.Source==CityEntryEventBuilder.Kind).Text);
        Step(400);Assert.Equal("两百米后，进入城区，请注意减速慢行。",queued.Last(q=>q.Source==CityEntryEventBuilder.Kind).Text);
        Assert.Single(playback.Pending,p=>p.Source==CityEntryEventBuilder.Kind);
        Step(601);Assert.DoesNotContain(playback.Pending,p=>p.Source==CityEntryEventBuilder.Kind);
        Assert.Equal(2,queued.Count(q=>q.Source==CityEntryEventBuilder.Kind));
    }

    [Fact]
    public void SharedCoordinatorReceivesEventsBeforeAnyPlaybackAndOwnsTriggerConsumption()
    {
        var scene=Scene();var guidance=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();guidance.Queued+=queued.Add;
        var runtime=new NavigationRuntime(guidance:guidance);
        var first=Data(scene.Route,500);
        runtime.Process(first,scene.Map,Now);
        Assert.Empty(queued);
        Assert.NotEmpty(guidance.Timeline.Sources.Single(p=>p.Source=="navigation").Events);
        guidance.Tick(first,Now);Assert.Single(queued);
        var hundred=Data(scene.Route,600);runtime.Process(hundred,scene.Map,Now);guidance.Tick(hundred,Now);
        Assert.Single(queued); // 72 km/h suppresses the hundred-metre point.
        var execute=Data(scene.Route,680);runtime.Process(execute,scene.Map,Now);guidance.Tick(execute,Now);
        Assert.Equal(2,queued.Count);Assert.Equal(0,queued[^1].Priority);
    }

    [Theory]
    [InlineData(1200, true)]
    [InlineData(800, true)]
    [InlineData(799, true)]
    [InlineData(100, true)]
    public void UnresolvedLoadedJoinLogsItsCauseAndDoesNotRequireEightHundredMetersForContinuation(float distance, bool speaks)
    {
        var map = new TruckLib.ScsMap.Map();
        var approach = Road.Add(map, new(0, 0, 0), new(0, 0, distance), default);
        var after = Road.Add(map, new(0, 0, distance+100), new(0, 0, distance+2100), default);
        ulong entry = approach.ForwardNode.Uid, exit = after.Node.Uid;
        LaneConnection[] lanes = [new(entry, 0, exit, 0), new(42, 0, exit, 1)];
        DirectedRoad main = new("prefab:ABC:0:1", entry, exit,
            [new(0, 0, distance), new(0, 0, distance), new(0, 0, distance+50),
             new(0, 0, distance+100), new(0, 0, distance+100)]) { LaneConnections = lanes };
        var side = main with { Id = "prefab:ABC:2:1", StartNodeUid = 42 };
        var nearby = new NearbyMapSnapshot(map, [], [], []) { PrefabPaths = [main, side] };
        var route = RouteSnapshot.Parse([new(approach.Node.Uid, distance+2100, 100),
            new(entry, 2100, 90), new(exit, 2000, 80), new(after.ForwardNode.Uid, 0, 0)], Now);
        var lines = new List<string>(); var runtime = new NavigationRuntime(log: lines.Add);
        var first = runtime.Process(Data(route, 0), nearby, Now);
        Assert.Equal(speaks ? NavigationPrompts.Continue() : null, first.LastSpoken);
        Assert.Equal(distance, first.ConfirmedAheadMeters!.Value, 1);
        Assert.Single(lines,l=>l.StartsWith("导航连接解析错误："));
        runtime.Process(Data(route, 1), nearby, Now);
        Assert.Equal(speaks ? 1 : 0, lines.Count(l => l.StartsWith("导航指令：")));
        Assert.Single(lines,l=>l.StartsWith("导航连接解析错误："));
    }

    private sealed class DelayedSynth : ISpeechSynthesizer
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> SynthesizeAsync(string text, CancellationToken token)
        {
            Started.TrySetResult(text);
            return await Result.Task.WaitAsync(token);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailedSynth : ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text, CancellationToken token)
            => Task.FromException<string>(new IOException("test synthesis failure"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(true,true,true)]
    [InlineData(false,true,false)]
    [InlineData(true,false,false)]
    public void YardExitUsesVerifiedAreaAndRoutePrefixWithoutInventingInternalRoads(bool inArea,bool towardExit,bool expected)
    {
        var scene=Scene();var source=scene.Route.Nodes.Skip(1).ToArray();
        var route=RouteSnapshot.Parse(source,Now);
        var area=new FreeNavigationArea("yard",[new(-15,0,650),new(15,0,650),new(15,0,700),new(-15,0,700)],
            [source[0].Uid]);
        var nearby=scene.Map with{FreeNavigationAreas=[area]};
        var data=Data(route,670);
        data=data with{Vehicle=data.Vehicle! with{X=inArea ? 0 : 30,HeadingTurns=towardExit ? .5 : 0}};
        int calls=0;var runtime=ObserveDiagnostics(()=>calls++);
        var first=runtime.Process(data,nearby,Now);
        if(expected)
        {
            Assert.NotNull(first.Next);Assert.Equal(30,first.Next.DistanceMeters,1);
            // At 20 m/s, the two-second cue is already due 30 m before the exit.
            Assert.Equal("右转。",first.LastSpoken);Assert.Equal(1,calls);
            var prefix=new RoadPathSource(scene.Map.Map,scene.Map.PrefabPaths).GetRoutePrefix(new(0,0,670),route,source[0].Uid);
            double prefixWorldLength=prefix.Sum(e=>e.Points.Zip(e.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second)));
            Assert.Equal(prefixWorldLength+30,Assert.Single(first.Upcoming,m=>m.Kind==ManeuverKind.Destination).DistanceMeters,1);
            var near=data with{Vehicle=data.Vehicle! with{Z=680}};
            Assert.Equal("右转。",runtime.Process(near,nearby,Now).LastSpoken);Assert.Equal(1,calls);
            runtime.Process(near,nearby,Now);Assert.Equal(1,calls);
        }
        else Assert.Null(first.Next);
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,false)]
    [InlineData(true,true)]
    public void OrdinaryStraightIsTransparentAndDoesNotDelayNextTurnBriefing(bool hasChoice,bool startInside)
    {
        var scene=Scene();var map=scene.Map.Map;
        var incoming=map.MapItems.Values.OfType<Road>().Single(r=>r.Node.Uid==scene.Route.Nodes[0].Uid);
        var straight=map.MapItems.Values.OfType<Road>().Single(r=>r.Node.Position.Z==760);
        straight.ForwardNode.Position=new(0,0,1200);
        var turn=Road.Add(map,new(-40,0,1240),new(-600,0,1240),default);
        var continuation=Road.Add(map,new(0,0,1260),new(0,0,1600),default);
        DirectedRoad[] links=[..scene.Map.PrefabPaths.Where(p=>hasChoice || p.EndNodeUid==straight.Node.Uid),
            new("prefab:DEF:0:1",straight.ForwardNode.Uid,turn.Node.Uid,[new(0,0,1200),new(-40,0,1240)]) { Junction = new(2, 2, 1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=-Vector3.UnitX },
            new("prefab:DEF:0:2",straight.ForwardNode.Uid,continuation.Node.Uid,[new(0,0,1200),new(0,0,1260)]) { Junction = new(2, 2, 1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ }];
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,2000,100),new(incoming.ForwardNode.Uid,1600,80),
            new(straight.Node.Uid,1500,75),new(straight.ForwardNode.Uid,1000,50),new(turn.Node.Uid,900,45),new(turn.ForwardNode.Uid,0,0)],Now);
        var nearby=scene.Map with{PrefabPaths=links};var guidance=new GuidanceCoordinator();
        var queued=new List<QueuedGuidanceSpeech>();guidance.Queued+=queued.Add;
        var runtime=new NavigationRuntime(guidance:guidance);
        NavigationDiagnostic Step(float z)
        {
            var data=Data(route,z);var result=runtime.Process(data,nearby,Now);guidance.Tick(data,Now);return result;
        }
        var first=Step(startInside?710:100);
        Assert.Equal(ManeuverKind.TurnRight,first.Next!.Kind);
        if(startInside)
        {
            Assert.False(first.DeferredUntilJunctionExit);
            Assert.Contains(queued,q=>q.Text=="四百五十米后，右转。");
            return;
        }
        Step(650);Step(680);var inside=Step(710);
        Assert.Contains(queued,q=>q.Text=="五百米后，右转。");
        Assert.DoesNotContain(queued,q=>q.Text=="直行。");
        if(hasChoice)Assert.Contains(first.Upcoming,m=>m.Kind==ManeuverKind.Straight);
    }

    [Fact]
    public void NextBriefingWaitsUntilThePreviousJunctionIsFullyTraversed()
    {
        var scene=Scene();var map=scene.Map.Map;
        var firstExit=map.MapItems.Values.OfType<Road>().Single(r=>r.ForwardNode.Uid==scene.Route.Nodes[3].Uid);
        var nextExit=Road.Add(map,new(-600,0,770),new(-600,0,1200),default);
        var otherExit=Road.Add(map,new(-640,0,730),new(-1000,0,730),default);
        DirectedRoad[] links=[..scene.Map.PrefabPaths,
            new("prefab:DEF:0:1",firstExit.ForwardNode.Uid,nextExit.Node.Uid,[new(-600,0,730),new(-600,0,770)]) { Junction = new(2, 2, 1),NativeEntryTangent=-Vector3.UnitX,NativeExitTangent=Vector3.UnitZ },
            new("prefab:DEF:0:2",firstExit.ForwardNode.Uid,otherExit.Node.Uid,[new(-600,0,730),new(-640,0,730)]) { Junction = new(2, 2, 1),NativeEntryTangent=-Vector3.UnitX,NativeExitTangent=-Vector3.UnitX }];
        var nearby=scene.Map with{PrefabPaths=links};
        var nodes=scene.Route.Nodes.Take(4).Select((n,i)=>n with{DistanceToEndMeters=2000-i*400}).ToList();
        nodes.Add(new(nextExit.Node.Uid,400,20));nodes.Add(new(nextExit.ForwardNode.Uid,0,0));
        var route=RouteSnapshot.Parse(nodes,Now);
        int calls=0;var runtime=ObserveDiagnostics(()=>calls++);
        runtime.Process(Data(route,680),nearby,Now);
        Assert.Equal(1,calls);
        var inside=Data(route,725);inside=inside with{Vehicle=inside.Vehicle! with{X=-10,HeadingTurns=.25}};
        var crossing=runtime.Process(inside,nearby,Now);
        Assert.True(crossing.DeferredUntilJunctionExit);
        Assert.Equal(1,calls);
        var outside=inside with{Vehicle=inside.Vehicle! with{X=-50,Z=730}};
        var completed=runtime.Process(outside,nearby,Now);
        Assert.False(completed.DeferredUntilJunctionExit);
        Assert.Equal(2,calls);
    }

    [Theory]
    [InlineData(-.0006,false)]
    [InlineData(0,false)]
    [InlineData(-.5,true)]
    public void StationaryVelocityNoiseDoesNotSuspendNavigationButRealReverseDoes(double speed,bool suspend)
    {
        var scene=Scene();var data=Data(scene.Route,500);
        data=data with{Vehicle=data.Vehicle! with{SpeedMetersPerSecond=speed}};
        var result=new NavigationRuntime().Process(data,scene.Map,Now);
        Assert.Equal(suspend,result.Next is null);
    }

    [Fact]
    public void LongConfirmedRoadAnnouncesOnceWithoutInventingTheNextTurnDistance()
    {
        var map = new TruckLib.ScsMap.Map();
        var road = Road.Add(map,new(0,0,0),new(0,0,4000),default);
        var route = RouteSnapshot.Parse([new(road.Node.Uid,4000,200),new(road.ForwardNode.Uid,100,5)],Now);
        var nearby = new NearbyMapSnapshot(map,[],[],[]);
        int calls = 0;
        var runtime = ObserveDiagnostics(()=>calls++);
        var first = runtime.Process(Data(route,100),nearby,Now);
        Assert.Equal(NavigationPrompts.Continue(),first.LastSpoken);
        Assert.Null(first.Next);
        Assert.InRange(first.ConfirmedAheadMeters!.Value,3899,3901);
        runtime.Process(Data(route,200),nearby,Now);
        runtime.Process(Data(route,200,paused:true),nearby,Now);
        runtime.Process(Data(route,300),nearby,Now);
        Assert.Equal(1,calls);
    }

    [Fact]
    public void UnloadedJunctionUsesCurrentConfirmedCorridorForNoDistanceContinuation()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map,new(0,0,0),new(0,0,400),default);
        var outgoing = Road.Add(map,new(0,0,450),new(0,0,4000),default);
        var route = RouteSnapshot.Parse([new(incoming.Node.Uid,4000,200),new(incoming.ForwardNode.Uid,3600,180),
            new(outgoing.Node.Uid,3550,175),new(outgoing.ForwardNode.Uid,0,0)],Now);
        int calls = 0;
        var runtime = ObserveDiagnostics(()=>calls++);
        var result = runtime.Process(Data(route,100),new(map,[],[],[]),Now);
        Assert.Equal(NavigationPrompts.Continue(),result.LastSpoken);
        Assert.Equal(300,result.ConfirmedAheadMeters!.Value,1);
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task FailedSynthesisFallsBackOnceRatherThanWaitingForever()
    {
        var scene = Scene();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new SpeechAnnouncementQueue(new FailedSynth(), _ => {},
            message => { if (message.StartsWith("TTS 合成失败")) failed.TrySetResult(); });
        int beeps = 0;
        var played=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var playback=new GuidancePlaybackManager(queue,new CallbackPlayer(_=>{beeps++;played.TrySetResult();}),()=>"beep.wav");
        var guidance=new GuidanceCoordinator(queue,playback);
        var runtime = new NavigationRuntime(queue,guidance:guidance);
        void Step(float z){var data=Data(scene.Route,z);guidance.Observe(data,Now);runtime.Process(data,scene.Map,Now);guidance.Tick(data,Now);}
        Step(621);
        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await played.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Step(621);Step(622);
        Assert.Equal(1, beeps);
    }

    [Fact]
    public async Task DynamicInstructionWaitsForReadyAudioWithoutConsumingItAsABeep()
    {
        var scene = Scene();
        var synth = new DelayedSynth();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<string>();
        int beeps = 0;
        string file = Path.GetTempFileName();
        string startFile = Path.GetTempFileName();
        try
        {
            await using var queue = new SpeechAnnouncementQueue(synth, played.Add,
                message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.TrySetResult(); },
                preGenerated:new Dictionary<string,string>{{NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Start,1000,60),startFile}});
            var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var playback=new GuidancePlaybackManager(queue,new CallbackPlayer(path=>
                {if(path==startFile)return;if(path=="beep.wav")beeps++;else played.Add(path);completed.TrySetResult();}),()=>"beep.wav");
            var guidance=new GuidanceCoordinator(queue,playback);
            var runtime = new NavigationRuntime(queue,guidance:guidance);
            void Step(float z){var data=Data(scene.Route,z);guidance.Observe(data,Now);runtime.Process(data,scene.Map,Now);guidance.Tick(data,Now);}
            Step(621);
            string text = await synth.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("右转", text);
            Assert.Empty(played);
            Assert.Equal(0, beeps);
            synth.Result.TrySetResult(file);
            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
            Step(621);Step(622);
            Assert.Single(played);
            Assert.Equal(0, beeps);
        }
        finally { File.Delete(file);File.Delete(startFile); }
    }
    [Fact]
    public async Task StandaloneAnalysisNeverPlaysEvenWhenItsSpeechIsAlreadyCached()
    {
        var scene=Scene();string file=Path.GetTempFileName();int directPlayback=0;
        try
        {
            const string text="五十米后，右转。";
            await using var queue=new SpeechAnnouncementQueue(new FailedSynth(),_=>directPlayback++,
                preGenerated:new Dictionary<string,string>{{text,file}});
            Assert.True(queue.IsReady(text));
            var runtime=new NavigationRuntime(queue);
            Assert.Equal(text,runtime.Process(Data(scene.Route,621),scene.Map,Now).LastSpoken);
            Assert.Equal(0,directPlayback);
        }
        finally{File.Delete(file);}
    }
    private sealed class CallbackPlayer(Action<string> callback):IGuidanceAudioPlayer
    {public Task PlayAsync(string path,CancellationToken token){callback(path);return Task.CompletedTask;}}

    [Fact]
    public void FirstDetectionAt79MetersRepeatsOnlyForTwentyMeterExecution()
    {
        var scene=Scene();var calls=0;var logs=new List<string>();
        var runtime=ObserveDiagnostics(()=>calls++,logs.Add);
        runtime.Process(Data(scene.Route,621),scene.Map,Now);
        runtime.Process(Data(scene.Route,651),scene.Map,Now);
        runtime.Process(Data(scene.Route,680),scene.Map,Now);
        Assert.Equal(2,calls);
        Assert.Contains(logs,l=>l.Contains("导航指令：右转。"));
        Assert.Contains(logs,l=>l.Contains("导航指令：五十米后，右转。"));
    }
    [Fact]
    public void OfficialRouteSkipsHundredAtHighSpeedAndKeepsExecution()
    {
        var scene=Scene();var calls=0;var logs=new List<string>();
        var runtime=ObserveDiagnostics(()=>calls++,logs.Add);
        var next=runtime.Process(Data(scene.Route,100),scene.Map,Now);
        Assert.Equal(ManeuverKind.TurnRight,next.Next!.Kind);
        Assert.InRange(next.Next.DistanceMeters,599,601);
        runtime.Process(Data(scene.Route,201),scene.Map,Now);
        runtime.Process(Data(scene.Route,501),scene.Map,Now);
        runtime.Process(Data(scene.Route,601),scene.Map,Now);
        runtime.Process(Data(scene.Route,651),scene.Map,Now);
        runtime.Process(Data(scene.Route,680),scene.Map,Now);
        runtime.Process(Data(scene.Route,690),scene.Map,Now);
        Assert.Equal(3,calls);
        Assert.Contains(logs,l=>l.Contains("导航指令：六百米后，右转。"));
        Assert.Contains(logs,l=>l.Contains("导航指令：右转。"));
    }
    [Fact]
    public void MissingNavigationPauseAndStalePoseNeverAnnounce()
    {
        var scene=Scene();var calls=0;
        var runtime=ObserveDiagnostics(()=>calls++);
        Assert.Null(runtime.Process(Data(scene.Route,500,RouteAvailability.None),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,paused:true),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,stale:true),scene.Map,Now).Next);
        Assert.Equal(0,calls);
    }
    [Fact]
    public void EveryRuntimePhraseExistsInPreGeneratedPack()
    {
        Assert.Equal(168,NavigationPrompts.AllTexts().Distinct().Count());
        Assert.Contains(NavigationPrompts.Continue(),LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
        foreach(string text in NavigationPrompts.AllTexts())
            Assert.Contains(text,LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
    }
}
