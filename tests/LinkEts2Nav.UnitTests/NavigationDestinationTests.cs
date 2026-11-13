using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationDestinationTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static readonly DirectedRoad Road=new("road",10,20,[Vector3.Zero,new(0,0,100),new(100,0,100)]);
    private static RouteSnapshot Route()=>RouteSnapshot.Parse([new(10,3800,60),new(20,0,0)],Now);

    [Fact]
    public void DestinationUsesWorldArcLengthAndStableTerminalIdentityInsteadOfScaledSdkRemaining()
    {
        var cue=NavigationDestination.Find(Route(),[Road],50,200,false)!;
        Assert.Equal(ManeuverKind.Destination,cue.Kind);
        Assert.Equal(150,cue.DistanceMeters);Assert.Equal(150,cue.EndDistanceMeters);
        Assert.Equal(cue.Id,NavigationDestination.Find(Route() with{Revision="recalculated"},[Road],75,200,false)!.Id);
        Assert.Null(cue.JunctionId);
    }

    [Theory]
    [InlineData(true,0,20,200,false)]
    [InlineData(false,100,20,200,false)]
    [InlineData(false,0,30,200,false)]
    [InlineData(false,0,20,199,false)]
    [InlineData(false,0,20,200,true)]
    public void MissingTruncatedOrUnconfirmedTerminalNeverTurnsTheLoadedWindowIntoDestination(
        bool truncated,float remaining,ulong terminal,double confirmed,bool unresolved)
    {
        var route=RouteSnapshot.Parse([new(10,3800,60),new(terminal,remaining,0)],Now) with{MayBeTruncated=truncated};
        Assert.Null(NavigationDestination.Find(route,[Road],50,confirmed,unresolved));
    }

    [Fact]
    public void PlannerPrearrangesDestinationAfterPreviousActionEndAndDoesNotCallItANumberedJunction()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var turn=new NavigationManeuver("turn",ManeuverKind.TurnLeft,100,"a","b"){EndDistanceMeters=150,JunctionId="turn"};
        var straight=new NavigationManeuver("straight",ManeuverKind.Straight,180,"b","c"){EndDistanceMeters=190,JunctionId="straight"};
        var destination=new NavigationManeuver("destination",ManeuverKind.Destination,300,"c","d"){EndDistanceMeters=300};
        var plan=planner.Build("s","r",Now,0,[turn,straight,destination],true,false,false,scheduler);
        var overview=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("destination@")&&t.Kind==SpeechTriggerKind.Overview);
        Assert.Equal(150,overview.AlongMeters);Assert.True(overview.StrictCrossing);
        Assert.Equal("一百五十米后，到达目的地附近。",overview.Text);
        Assert.DoesNotContain(plan.Triggers,t=>t.Id.Contains(":through:"));
        Assert.Equal("目的地就在附近，请留意停车位置。",Assert.Single(plan.Triggers,
            t=>t.EventId.StartsWith("destination@")&&t.Kind==SpeechTriggerKind.Execution).Text);
        timeline.Publish(plan);scheduler.Update(timeline,Now,0);
        timeline.Publish(plan with{PlayerAlongMeters=280});
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued&&r.Trigger.EventId.StartsWith("destination@"));
        Assert.Empty(scheduler.Update(timeline,Now,0));
    }

    [Fact]
    public void DestinationSharesAllOrdinaryDistanceStagesAndTheTwentyMeterExecutionPoint()
    {
        var cue=new NavigationManeuver("destination",ManeuverKind.Destination,6000,"a","b"){EndDistanceMeters=6000};
        var plan=new NavigationEventPlanner().Build("s","r",Now,0,[cue],true,false,false,new());
        foreach(int stage in new[]{2000,1000,500,200,100})
            Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Advance&&t.AlongMeters==6000-stage
                &&t.Text==NavigationPrompts.AtDistance(ManeuverKind.Destination,stage));
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Execution&&t.AlongMeters==5980);
    }

    [Fact]
    public void RuntimePublishesActualRouteTailDestinationAndSpeaksOnlyOnceAtExecution()
    {
        var map=new TruckLib.ScsMap.Map();var road=TruckLib.ScsMap.Road.Add(map,Vector3.Zero,new(0,0,200),default);
        var route=RouteSnapshot.Parse([new(road.Node.Uid,3800,60),new(road.ForwardNode.Uid,0,0)],Now);
        var spoken=new List<string>();var runtime=new NavigationRuntime(log:spoken.Add);var nearby=new NearbyMapSnapshot(map,[],[],[]);
        GameDataSnapshot Data(double z)=>new(new(0,0,z,.5,0,380,true,false,1,Now),route,RouteAvailability.Available,"s",true);
        var first=runtime.Process(Data(50),nearby,Now);
        Assert.Equal(150,Assert.Single(first.Upcoming).DistanceMeters,1);
        Assert.Equal(ManeuverKind.Destination,first.Next!.Kind);
        Assert.Equal("一百五十米后，到达目的地附近。",first.LastSpoken);
        Assert.Equal("目的地就在附近，请留意停车位置。",runtime.Process(Data(180),nearby,Now).LastSpoken);
        Assert.Equal("目的地就在附近，请留意停车位置。",runtime.Process(Data(181),nearby,Now).LastSpoken);
        Assert.Single(spoken,s=>s.StartsWith("导航指令：目的地就在附近",StringComparison.Ordinal));
    }
}
