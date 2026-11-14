using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Map;
using System.Numerics;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidanceCoordinatorTests
{
    [Fact]
    public async Task ReplacementWithdrawsQueuedUnknownTextBeforeTheKnownDistanceCanBeQueued()
    {
        await using var playback=new GuidancePlaybackManager(null,new NoPlayer(),()=>null);
        var coordinator=new GuidanceCoordinator(playback:playback);
        coordinator.Publish(new("navigation","s","r",0,true,Now,[GuidanceTestSeries.Route("unknown",10000,
            [new("unknown",0,"unknown")],[new("unknown:first","unknown",SpeechTriggerKind.Overview,0,"unknown")])]){CoordinateFrame="f"});
        coordinator.Tick(Data(),Now);Assert.Equal("unknown",Assert.Single(playback.Pending).Text);
        var replacement=new GuidanceSourcePlan("navigation","s","r",1,true,Now,[GuidanceTestSeries.Route("known",9400,
            [new("known",9400,"known")],[new("known:first","known",SpeechTriggerKind.Overview,0,"9.4km")])])
            {CoordinateFrame="f",ReplacedSequenceIds=["unknown"]};
        coordinator.Publish(replacement);Assert.Empty(playback.Pending);
        coordinator.Tick(Data(),Now);Assert.Equal("9.4km",Assert.Single(playback.Pending).Text);
        coordinator.Publish(replacement with{PlayerAlongMeters=2});coordinator.Tick(Data(),Now);
        Assert.Equal("9.4km",Assert.Single(playback.Pending).Text);
        Assert.DoesNotContain(coordinator.Timeline.Sources[0].Sequences,s=>s.Id=="unknown");
    }
    [Fact]
    public void StartAndReplanQueueImmediatelyEvenWhenVehicleIsFarOutsideTheRoute()
    {
        var map=new TruckLib.ScsMap.Map();
        var road=TruckLib.ScsMap.Road.Add(map,Vector3.Zero,new(300,0,0),default);
        var estimator=new RouteJourneyEstimator(map,(_,_)=>new(80,"category"));
        var coordinator=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        coordinator.JourneyEstimateProvider=data=>estimator.Estimate(data.Route);
        GameDataSnapshot At(int seconds,bool reversed)
        {
            var time=Now.AddSeconds(seconds);
            ulong a=reversed?road.ForwardNode.Uid:road.Node.Uid,b=reversed?road.Node.Uid:road.ForwardNode.Uid;
            var route=RouteSnapshot.Parse([new RouteNode(a,9000,600),new RouteNode(b,0,0)],time);
            return new(new(100000,10000,-100000,.5,0,9000,true,false,(ulong)(seconds+1),time),route,RouteAvailability.Available,"off-route",true);
        }
        coordinator.Observe(At(0,false),Now);
        Assert.Equal("准备出发，全程三百米，大约需要一分钟。",Assert.Single(queued).Text);
        coordinator.Observe(At(1,true),Now.AddSeconds(1));
        Assert.Equal(2,queued.Count);
        Assert.Equal("重新规划路线，全程三百米，大约需要一分钟。",queued[1].Text);
    }

    [Fact]
    public void JourneyBriefingWaitsForCompleteMetadataAndIsNotRecomputedEveryFrame()
    {
        var coordinator=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        RouteJourneyEstimate? available=null;int calls=0;
        coordinator.JourneyEstimateProvider=_=>{calls++;return available;};
        GameDataSnapshot DataAt(double seconds,ulong[]? nodes=null)
        {
            var time=Now.AddSeconds(seconds);nodes??=[1,2,3];
            var route=RouteSnapshot.Parse(nodes.Select((uid,i)=>new RouteNode(uid,(nodes.Length-i-1)*1000,10)),time);
            return new(new(0,0,0,0,10,1000,true,false,(ulong)(seconds*10+1),time),route,RouteAvailability.Available,"s",true);
        }
        coordinator.Observe(DataAt(0),Now);Assert.Empty(queued);
        available=new(2100,61,null);coordinator.Observe(DataAt(1),Now.AddSeconds(1));
        Assert.Equal("准备出发，全程二点一公里，大约需要两分钟。",Assert.Single(queued).Text);
        coordinator.Observe(DataAt(2),Now.AddSeconds(2));Assert.Equal(2,calls);
        coordinator.Observe(DataAt(3,[1,4,5]),Now.AddSeconds(3));
        Assert.Equal("重新规划路线，全程二点一公里，大约需要两分钟。",queued.Last().Text);
        Assert.Equal(3,calls);
    }

    [Fact]
    public async Task EndingNavigationClearsSpatialSpeechWithoutAnArrivalAnnouncement()
    {
        await using var manager=new GuidancePlaybackManager(null,new NoPlayer(),()=>null);
        var c=new GuidanceCoordinator(playback:manager);
        GameDataSnapshot Step(double seconds,double remaining,RouteAvailability availability)
        {
            var time=Now.AddSeconds(seconds);var route=RouteSnapshot.Parse([new RouteNode(1,100,10),new RouteNode(2,0,0)],time);
            return new(new(0,0,0,0,10,remaining,true,false,(ulong)(seconds*10+1),time),route,availability,"s",true);
        }
        var start=Step(0,100,RouteAvailability.Available);c.Observe(start,Now);
        c.Publish(new("navigation","s",start.Route.Revision,0,true,Now,[GuidanceTestSeries.Route("turn",100,
            [new("turn",50,"left")],[new("left","turn",SpeechTriggerKind.Execution,0,"left")])]));
        c.Tick(start,Now);Assert.Single(manager.Pending);
        foreach(double seconds in new[]{1d,1.5,2})c.Observe(Step(seconds,0,RouteAvailability.None),Now.AddSeconds(seconds));
        Assert.DoesNotContain(c.Timeline.Sources,p=>p.Source=="navigation");
        Assert.Empty(manager.Pending);
    }

    [Fact]
    public void NotificationsUseSessionScopedIdentityWithoutPublishingASpatialSeries()
    {
        var coordinator=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        coordinator.Notify("journey","s",new("start","导航开始。"));
        coordinator.Notify("journey","s",new("start","导航开始。"));
        Assert.True(Assert.Single(queued).IsNotification);Assert.Empty(coordinator.Timeline.Sources);
        coordinator.Notify("journey","next-session",new("start","导航开始。"));
        Assert.Equal(2,queued.Count);
    }

    [Fact]
    public async Task DisappearanceDoesNotPreventTheLaterEndFromClearingPendingSpeech()
    {
        await using var playback=new GuidancePlaybackManager(null,new NoPlayer(),()=>null);
        var c=new GuidanceCoordinator(playback:playback);
        c.Publish(new("e","s","r",0,true,Now,[GuidanceTestSeries.Route("camera",150,[new("camera",100,"camera")],
            [new("first","camera",SpeechTriggerKind.Advance,0,"camera")])]){CoordinateFrame="f"});
        c.Tick(Data(),Now);Assert.Single(playback.Pending);
        foreach(double player in new[]{110d,150d,160d})
        {
            c.Publish(new("e","s","r",player,true,Now,[]){CoordinateFrame="f"});c.Tick(Data(),Now);
            if(player<=150)Assert.Single(playback.Pending);else Assert.Empty(playback.Pending);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCameraStagesSurviveANavigationToCameraFrameChange(bool temporarilyMissing)
    {
        var c=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        var snapshot=new WarningSnapshot("s","r",true,true,Now,[]);
        PlannedCameraWarning[] Plans(double distance)=>[new("shared",500,distance,WarningDistanceKind.Road,Math.Max(0,distance-500)),
            new("shared",200,distance,WarningDistanceKind.Road,Math.Max(0,distance-200))];
        void Navigation(double player)=>c.Publish(new("navigation","s","r",player,true,Now,[]){CoordinateFrame="nav"});
        Navigation(1000);c.PublishLandmarks(snapshot,Plans(600),observations:Cameras);c.Tick(Data(),Now);
        Navigation(1100);c.PublishLandmarks(snapshot,Plans(500),observations:Cameras);c.Tick(Data(),Now);
        Assert.StartsWith("五百米",Assert.Single(queued).Text);
        c.Suspend("navigation");
        if(temporarilyMissing){c.PublishLandmarks(snapshot,[],cameraPlayerAlong:200,cameraFrame:"camera",observations:Cameras);c.Tick(Data(),Now);}
        c.PublishLandmarks(snapshot,Plans(400),cameraPlayerAlong:200,cameraFrame:"camera",observations:Cameras);c.Tick(Data(),Now);
        Assert.Single(queued);Assert.Contains(c.Timeline.Sources.Single(s=>s.Source=="landmarks").Triggers,t=>t.Id.EndsWith(":200"));
        c.PublishLandmarks(snapshot,Plans(200),cameraPlayerAlong:400,cameraFrame:"camera",observations:Cameras);c.Tick(Data(),Now);
        Assert.Equal("两百米处，有测速照相。",queued[^1].Text);Assert.Equal(2,queued.Count);
    }
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static readonly CameraObservation[] Cameras=new[]{"unknown","known","detour","camera"}
        .Select(id=>new CameraObservation(id,new(100,0,0),new(100,0,0),Vector3.UnitX,true,"test","test")).ToArray();
    private static GameDataSnapshot Data()=>new(new(0,0,0,0,10,1000,true,false,1,Now),RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true);
    private sealed class NoPlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>throw new InvalidOperationException("No audio is ready in this test.");}
    [Fact]
    public void PhysicalProximityBoundaryDoesNotBlockTheSeparateCalibratedCameraChain()
    {
        var c=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        c.PublishLandmarks(new("s","r",false,true,Now,[]),
            [new("unknown",500,300,WarningDistanceKind.Euclidean,0){Kind=CameraWarningKind.UncalibratedProximity},
             new("known",500,450,WarningDistanceKind.Road,0)],observations:Cameras);
        c.Tick(Data(),Now);
        Assert.Contains(queued,q=>q.EventId.Contains("unknown")&&q.Text==PlannedSpeechAnnouncer.UncalibratedText);
        Assert.Contains(queued,q=>q.EventId.Contains("known:Calibrated")&&q.Text=="四百五十米处，有测速照相。");
        Assert.DoesNotContain(queued,q=>q.Text.Contains("零米"));
    }
    [Fact]
    public void EuclideanFallbackStillHonorsTheKnownRouteLengthLowerBound()
    {
        var c=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        c.PublishLandmarks(new("s","r",true,true,Now,[]),
            [new("detour",500,100,WarningDistanceKind.Euclidean,1500),
             new("detour",200,100,WarningDistanceKind.Euclidean,1800)],observations:Cameras);
        c.Tick(Data(),Now);Assert.Empty(queued);
        Assert.Empty(c.Timeline.Sources[0].Triggers);
        c.PublishLandmarks(new("s","r",true,true,Now,[]),
            [new("detour",500,100,WarningDistanceKind.Euclidean,0),
             new("detour",200,100,WarningDistanceKind.Euclidean,250)],observations:Cameras);
        c.Tick(Data(),Now);
        Assert.Equal("一百米处，有测速照相。",Assert.Single(queued).Text);
        Assert.Single(c.Timeline.Sources[0].Triggers);
    }
    [Fact]
    public async Task ForwardRouteTrimmingKeepsPendingSpeechWhileReroutingWithdrawsOnlyRemovedFutureEvents()
    {
        await using var manager=new GuidancePlaybackManager(null,new NoPlayer(),()=>null,cancellationToken:TestContext.Current.CancellationToken);
        var c=new GuidanceCoordinator(playback:manager);
        GameDataSnapshot Route(params ulong[] nodes)
        {
            var route=RouteSnapshot.Parse(nodes.Select(uid=>new RouteNode(uid,1000,10)).ToArray(),Now);
            return Data() with{Route=route,RouteAvailability=RouteAvailability.Available};
        }
        void Publish(GameDataSnapshot data,params string[] ids)
        {
            c.Observe(data,Now);
            c.Publish(new("navigation","s",data.Route.Revision,0,true,Now,
                ids.Select(id=>GuidanceTestSeries.Route(id,100,[new(id,100,id)],
                    [new(id+":cue",id,SpeechTriggerKind.Advance,0,id)])).ToArray()));
            c.Tick(data,Now);
        }
        Publish(Route(1,2,3),"a","b");Assert.Equal(2,manager.Pending.Count(p=>!p.IsNotification));
        Publish(Route(2,3),"b");Assert.Equal(2,manager.Pending.Count(p=>!p.IsNotification));
        Publish(Route(2,9));Assert.Equal("a",Assert.Single(manager.Pending,p=>!p.IsNotification).EventId);
        Publish(Route(2,3),"b");Assert.Equal(2,manager.Pending.Count(p=>!p.IsNotification));
    }

    [Fact]
    public void CameraPassageAndRenewedApproachCreateANewOrdinaryEventEncounter()
    {
        var c=new GuidanceCoordinator();var engine=new CameraWarningEngine();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        void Step(double distance,CameraApproach approach)
        {
            var snapshot=new WarningSnapshot("s","r",true,true,Now,[new("camera",approach,Math.Abs(distance),distance)]);
            var plans=engine.GetPlans(snapshot,Now);
            c.PublishLandmarks(snapshot,plans,engine.PassedEncounters,observations:Cameras);c.Tick(Data(),Now);
        }
        Step(600,CameraApproach.RouteAhead);Assert.Empty(queued);
        Step(490,CameraApproach.RouteAhead);Step(190,CameraApproach.RouteAhead);Assert.Equal(2,queued.Count);
        Step(-10,CameraApproach.Behind);Step(-60,CameraApproach.Behind);Step(60,CameraApproach.RouteAhead);
        Assert.Equal(3,queued.Count);Assert.NotEqual(queued[1].EventId,queued[2].EventId);
    }

    [Fact]
    public void LandmarkHasOrdinaryAdvancePointsAndDynamicTextWithoutAnExecutionPoint()
    {
        var coordinator=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        var snapshot=new WarningSnapshot("s","r",true,true,Now,[]);
        PlannedCameraWarning[] Plans(int limit)=>[new("camera",500,500,WarningDistanceKind.Road,0){SpeedLimitKph=limit},
            new("camera",200,500,WarningDistanceKind.Road,300){SpeedLimitKph=limit}];
        coordinator.PublishLandmarks(snapshot,Plans(50),observations:Cameras);
        Assert.All(coordinator.Timeline.Sources[0].Triggers,t=>Assert.Equal(SpeechTriggerKind.Advance,t.Kind));
        coordinator.Tick(Data(),Now);Assert.Contains("五十",Assert.Single(queued).Text);
        coordinator.PublishLandmarks(snapshot,Plans(80),observations:Cameras);coordinator.Tick(Data(),Now);
        Assert.Single(queued);Assert.Contains("八十",coordinator.Timeline.Sources[0].Triggers[0].Text);
        coordinator.PublishLandmarks(snapshot,[new("camera",200,190,WarningDistanceKind.Road,0){SpeedLimitKph=80}],observations:Cameras);
        coordinator.Tick(Data(),Now);Assert.Equal(2,queued.Count);Assert.Contains("八十",queued[^1].Text);
    }
    [Fact]
    public void RoadLandmarksUseTheNavigationCoordinateFrameWithoutAcquiringExecutionStages()
    {
        var c=new GuidanceCoordinator();
        c.Publish(new("navigation","s","r",1200,true,Now,[]){CoordinateFrame="frame"});
        c.PublishLandmarks(new("s","r",true,true,Now,[]),
            [new("camera",500,600,WarningDistanceKind.Road,100),new("camera",200,600,WarningDistanceKind.Road,400)],observations:Cameras);
        var plan=Assert.Single(c.Timeline.Sources,p=>p.Source=="landmarks");
        Assert.Equal(1200,plan.PlayerAlongMeters);
        Assert.Equal(1800,Assert.Single(plan.Events).AlongMeters);
        Assert.Equal(new double[]{1300,1600},plan.Triggers.Select(t=>t.AlongMeters));
        Assert.All(plan.Triggers,t=>Assert.Equal(SpeechTriggerKind.Advance,t.Kind));
    }
    [Fact]
    public void ExecutionAndOrdinaryDistancePointsShareOneTriggerTableAndPriority()
    {
        var c=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        c.Publish(new("navigation","s","r",0,true,Now,[GuidanceTestSeries.Route("turn",50,[new("turn",20,"turn")],
            [new("execute","turn",SpeechTriggerKind.Execution,0,"左转。"){Priority=0}])]));
        c.PublishLandmarks(new("s","r",true,true,Now,[]),[new("camera",200,200,WarningDistanceKind.Road,0)],observations:Cameras);
        c.Tick(Data(),Now);Assert.Equal(2,queued.Count);
        Assert.Equal(0,Assert.Single(queued,q=>q.Text=="左转。").Priority);
        Assert.Equal(10,Assert.Single(queued,q=>q.EventId.StartsWith("landmark:")).Priority);
    }
}
