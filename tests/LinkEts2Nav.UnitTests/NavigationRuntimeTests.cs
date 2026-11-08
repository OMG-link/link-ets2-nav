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
                [new(0,0,700),new(0,0,710),new(-10,0,725),new(-30,0,730)]),
            new("prefab:ABC:0:2",incoming.ForwardNode.Uid,straight.Node.Uid,[new(0,0,700),new(0,0,760)])];
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,1300,60),
            new(incoming.ForwardNode.Uid,600,30),new(right.Node.Uid,570,28),new(right.ForwardNode.Uid,0,0)],Now);
        return(new(map,[],[],[]){PrefabPaths=links},route);
    }
    private static GameDataSnapshot Data(RouteSnapshot route,float z,RouteAvailability availability=RouteAvailability.Available,
        bool paused=false,bool stale=false)=>new(
        new(0,0,z,.5,20,1000,true,paused,1,stale?Now.AddSeconds(-3):Now),
        route,availability,"session",!paused);
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

    [Fact]
    public void LongConfirmedRoadAnnouncesOnceWithoutInventingTheNextTurnDistance()
    {
        var map = new TruckLib.ScsMap.Map();
        var road = Road.Add(map,new(0,0,0),new(0,0,4000),default);
        var route = RouteSnapshot.Parse([new(road.Node.Uid,4000,200),new(road.ForwardNode.Uid,0,0)],Now);
        var nearby = new NearbyMapSnapshot(map,[],[],[]);
        int calls = 0;
        var runtime = new NavigationRuntime(fallback:()=>calls++);
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
    public void UnknownNearbyJunctionCannotBorrowLongRoadFallbackFromALaterChunk()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map,new(0,0,0),new(0,0,400),default);
        var outgoing = Road.Add(map,new(0,0,450),new(0,0,4000),default);
        var route = RouteSnapshot.Parse([new(incoming.Node.Uid,4000,200),new(incoming.ForwardNode.Uid,3600,180),
            new(outgoing.Node.Uid,3550,175),new(outgoing.ForwardNode.Uid,0,0)],Now);
        int calls = 0;
        var runtime = new NavigationRuntime(fallback:()=>calls++);
        var result = runtime.Process(Data(route,100),new(map,[],[],[]),Now);
        Assert.Null(result.LastSpoken);
        Assert.Equal(0,calls);
    }

    [Fact]
    public async Task FailedSynthesisFallsBackOnceRatherThanWaitingForever()
    {
        var scene = Scene();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new SpeechAnnouncementQueue(new FailedSynth(), _ => {},
            message => { if (message.StartsWith("TTS 合成失败")) failed.TrySetResult(); });
        int beeps = 0;
        var runtime = new NavigationRuntime(queue, () => beeps++);
        runtime.Process(Data(scene.Route,621), scene.Map, Now);
        await failed.Task.WaitAsync(TestContext.Current.CancellationToken);
        runtime.Process(Data(scene.Route,621), scene.Map, Now);
        runtime.Process(Data(scene.Route,622), scene.Map, Now);
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
        try
        {
            await using var queue = new SpeechAnnouncementQueue(synth, played.Add,
                message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.TrySetResult(); });
            var runtime = new NavigationRuntime(queue, () => beeps++);
            runtime.Process(Data(scene.Route,621), scene.Map, Now);
            string text = await synth.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("右转", text);
            Assert.Empty(played);
            Assert.Equal(0, beeps);
            synth.Result.TrySetResult(file);
            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
            runtime.Process(Data(scene.Route,621), scene.Map, Now);
            runtime.Process(Data(scene.Route,622), scene.Map, Now);
            Assert.Single(played);
            Assert.Equal(0, beeps);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void FirstDetectionAt79MetersDoesNotRepeatAt50Meters()
    {
        var scene=Scene();var calls=0;var logs=new List<string>();
        var runtime=new NavigationRuntime(fallback:()=>calls++,log:logs.Add);
        runtime.Process(Data(scene.Route,621),scene.Map,Now);
        runtime.Process(Data(scene.Route,651),scene.Map,Now);
        runtime.Process(Data(scene.Route,680),scene.Map,Now);
        Assert.Equal(1,calls);
        Assert.Contains(logs,l=>l.Contains("导航指令：五十米后，在下一个路口右转。"));
    }
    [Fact]
    public void OfficialRouteProducesThreeRightTurnStagesWithoutCameraOrLaneData()
    {
        var scene=Scene();var calls=0;var logs=new List<string>();
        var runtime=new NavigationRuntime(fallback:()=>calls++,log:logs.Add);
        var next=runtime.Process(Data(scene.Route,100),scene.Map,Now);
        Assert.Equal(ManeuverKind.TurnRight,next.Next!.Kind);
        Assert.InRange(next.Next.DistanceMeters,599,601);
        runtime.Process(Data(scene.Route,201),scene.Map,Now);
        runtime.Process(Data(scene.Route,501),scene.Map,Now);
        runtime.Process(Data(scene.Route,651),scene.Map,Now);
        runtime.Process(Data(scene.Route,653),scene.Map,Now);
        Assert.Equal(3,calls);
        Assert.Contains(logs,l=>l.Contains("导航指令：六百米后，在下一个路口右转。"));
        Assert.Contains(logs,l=>l.Contains("导航指令：右转。"));
    }
    [Fact]
    public void MissingNavigationPauseAndStalePoseNeverAnnounce()
    {
        var scene=Scene();var calls=0;
        var runtime=new NavigationRuntime(fallback:()=>calls++);
        Assert.Null(runtime.Process(Data(scene.Route,500,RouteAvailability.None),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,paused:true),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,stale:true),scene.Map,Now).Next);
        Assert.Equal(0,calls);
    }
    [Fact]
    public void EveryRuntimePhraseExistsInPreGeneratedPack()
    {
        Assert.Equal(84,NavigationPrompts.AllTexts().Distinct().Count());
        Assert.Contains(NavigationPrompts.Continue(),LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
        foreach(string text in NavigationPrompts.AllTexts())
            Assert.Contains(text,LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
    }
}
