using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidanceCoordinatorTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static GameDataSnapshot Data()=>new(new(0,0,0,0,10,1000,true,false,1,Now),RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true);
    private sealed class NoPlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>throw new InvalidOperationException("No audio is ready in this test.");}
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
                ids.Select(id=>new GuidanceEvent(id,100,id)).ToArray(),
                ids.Select(id=>new SpeechTrigger(id+":cue",id,SpeechTriggerKind.Advance,0,id)).ToArray()));
            c.Tick(data,Now);
        }
        Publish(Route(1,2,3),"a","b");Assert.Equal(2,manager.Pending.Count);
        Publish(Route(2,3),"b");Assert.Equal(2,manager.Pending.Count);
        Publish(Route(2,9));Assert.Equal("a",Assert.Single(manager.Pending).EventId);
        Publish(Route(2,3),"b");Assert.Equal(2,manager.Pending.Count);
    }

    [Fact]
    public void CameraPassageAndRenewedApproachCreateANewOrdinaryEventEncounter()
    {
        var c=new GuidanceCoordinator();var engine=new CameraWarningEngine();var queued=new List<QueuedGuidanceSpeech>();c.Queued+=queued.Add;
        void Step(double distance,CameraApproach approach)
        {
            var snapshot=new WarningSnapshot("s","r",true,true,Now,[new("camera",approach,Math.Abs(distance),distance)]);
            c.PublishLandmarks(snapshot,engine.GetPlans(snapshot,Now));c.Tick(Data(),Now);
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
        coordinator.PublishLandmarks(snapshot,Plans(50));
        Assert.All(coordinator.Timeline.Sources[0].Triggers,t=>Assert.Equal(SpeechTriggerKind.Advance,t.Kind));
        coordinator.Tick(Data(),Now);Assert.Contains("五十",Assert.Single(queued).Text);
        coordinator.PublishLandmarks(snapshot,Plans(80));coordinator.Tick(Data(),Now);
        Assert.Single(queued);Assert.Contains("八十",coordinator.Timeline.Sources[0].Triggers[0].Text);
        coordinator.PublishLandmarks(snapshot,[new("camera",200,190,WarningDistanceKind.Road,0){SpeedLimitKph=80}]);
        coordinator.Tick(Data(),Now);Assert.Equal(2,queued.Count);Assert.Contains("八十",queued[^1].Text);
    }
    [Fact]
    public void RoadLandmarksUseTheNavigationCoordinateFrameWithoutAcquiringExecutionStages()
    {
        var c=new GuidanceCoordinator();
        c.Publish(new("navigation","s","r",1200,true,Now,[],[]){CoordinateFrame="frame"});
        c.PublishLandmarks(new("s","r",true,true,Now,[]),
            [new("camera",500,600,WarningDistanceKind.Road,100),new("camera",200,600,WarningDistanceKind.Road,400)]);
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
        c.Publish(new("navigation","s","r",0,true,Now,[new("turn",20,"turn")],
            [new("execute","turn",SpeechTriggerKind.Execution,0,"左转。"){Priority=0}]));
        c.PublishLandmarks(new("s","r",true,true,Now,[]),[new("camera",200,200,WarningDistanceKind.Road,0)]);
        c.Tick(Data(),Now);Assert.Equal(2,queued.Count);Assert.Equal("左转。",queued[0].Text);
        Assert.Equal(10,queued[1].Priority);
    }
}
