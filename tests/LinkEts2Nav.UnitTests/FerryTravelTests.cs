using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class FerryTravelTests
{
    private static readonly DateTimeOffset Now=new(2026,11,14,0,0,0,TimeSpan.Zero);
    private static VehicleSnapshot Pose(double x,double seconds,ulong clock=1,bool sdk=true)
        =>new(x,0,0,0,0,1000,sdk,false,clock,Now.AddSeconds(seconds));
    private static FerryTelemetrySample Raw(bool flag,string from="",string to="")=>new(flag,from,to,100);
    private static FerryTravelExpectation Expectation()
    {
        FerryPort Port(string id,ulong node,float x)=>new(id,id,node+100,node,new(x,0,0),node+200,[node+300],false);
        var from=Port("a",1,0);var to=Port("b",2,10000);
        FreeNavigationArea Area(string id,float x)=>new(id,[new(x-20,0,-20),new(x+20,0,-20),new(x+20,0,20),new(x-20,0,20)],[]);
        return new(new(from,to,301,302,999),Now,[Area("from",0)],[Area("to",10000)]);
    }
    private static GameSnapshotStore Store(bool initialFlag=false,string initialFrom="",string initialTo="")
    {
        var store=new GameSnapshotStore();store.UpdateVehicle(Pose(0,0),Raw(initialFlag,initialFrom,initialTo));
        store.UpdateRoute(RouteSnapshot.Parse([new(301,1000,10),new(999,0,0)],Now));
        store.ExpectFerry(Expectation());return store;
    }

    [Fact]
    public void ToggleThenLatePayloadIsConfirmedBeforeTheSpaceSessionChanges()
    {
        var store=Store();string before=store.Capture(Now).SessionId;
        store.UpdateVehicle(Pose(10000,.1,2),Raw(true));
        Assert.True(store.Capture(Now.AddSeconds(.1)).IsTransportPending);
        Assert.False(store.Capture(Now.AddSeconds(.1)).IsVehicleUsable);
        store.UpdateVehicle(Pose(10000,.2,3),Raw(true,"a","b"));
        Assert.True(store.Capture(Now.AddSeconds(.2)).IsTransportPending);
        store.UpdateVehicle(Pose(10000,.3,4),Raw(true,"a","b"));
        var arrived=store.Capture(Now.AddSeconds(.3));
        Assert.False(arrived.IsTransportPending);Assert.True(arrived.IsVehicleUsable);
        Assert.NotEqual(before,arrived.SessionId);Assert.NotNull(arrived.FerryArrival);
        Assert.Equal(before,arrived.FerryArrival.PreviousSessionId);Assert.Equal(arrived.SessionId,arrived.FerryArrival.CurrentSessionId);
        Assert.Equal("a",arrived.FerryUse!.SourceId);Assert.Equal(RouteAvailability.Available,arrived.RouteAvailability);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void AStableOldNonemptyPayloadDoesNotConsumeTheNewEventEdge(bool initialFlag)
    {
        var store=Store(initialFlag,"oldA","oldB");
        store.UpdateVehicle(Pose(0,.1,2),Raw(!initialFlag,"oldA","oldB"));
        store.UpdateVehicle(Pose(0,.2,3),Raw(!initialFlag,"oldA","oldB"));
        store.UpdateVehicle(Pose(10000,.3,4),Raw(!initialFlag,"a","b"));
        store.UpdateVehicle(Pose(10000,.4,5),Raw(!initialFlag,"a","b"));
        Assert.NotNull(store.Capture(Now.AddSeconds(.4)).FerryArrival);
    }

    [Theory]
    [InlineData(true,"a","b",true,"a","b")] // A latched event at connection is not new.
    [InlineData(false,"","",false,"a","b")] // Payload changed without an edge.
    [InlineData(false,"","",true,"a","other")] // An unrelated event does not prove this crossing.
    public void MissingOrUnrelatedEventExpiresAsAnOrdinaryTeleport(bool oldFlag,string oldFrom,string oldTo,bool flag,string from,string to)
    {
        var store=Store(oldFlag,oldFrom,oldTo);string before=store.Capture(Now).SessionId;
        store.UpdateVehicle(Pose(10000,.1,2),Raw(flag,from,to));
        store.UpdateVehicle(Pose(10000,.2,3),Raw(flag,from,to));
        var expired=store.Capture(Now.AddSeconds(1.2)); // No further SDK sample is required to expire.
        Assert.False(expired.IsTransportPending);Assert.Null(expired.FerryArrival);Assert.NotEqual(before,expired.SessionId);
        Assert.Equal(RouteAvailability.Unknown,expired.RouteAvailability);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void SdkReconnectAndSimulationRewindAreNeverExempted(bool sdkChange)
    {
        var store=Store();store.UpdateVehicle(Pose(0,.1,10),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(0,.2,11),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(10000,.3,sdkChange?12UL:1UL,!sdkChange),Raw(true,"a","b"));
        var data=store.Capture(Now.AddSeconds(.3));Assert.Null(data.FerryArrival);Assert.False(data.IsTransportPending);
    }

    [Fact]
    public void EventAndLocationMustBothMatchTheRegisteredDestination()
    {
        var store=Store();store.UpdateVehicle(Pose(0,.1,2),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(0,.2,3),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(12000,.3,4),Raw(true,"a","b"));
        var data=store.Capture(Now.AddSeconds(.3));Assert.Null(data.FerryArrival);Assert.False(data.IsTransportPending);
    }

    [Fact]
    public void ArrivalUsesTheRealUnloadPositionInsteadOfAnActivationPointRadius()
    {
        var store=Store();var expected=Expectation();
        expected=expected with{Leg=expected.Leg with{Target=expected.Leg.Target with{UnloadPosition=new(12000,0,0)}},ArrivalAreas=[]};
        store.ExpectFerry(expected);store.UpdateVehicle(Pose(0,.1,2),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(0,.2,3),Raw(true,"a","b"));store.UpdateVehicle(Pose(12000,.3,4),Raw(true,"a","b"));
        Assert.NotNull(store.Capture(Now.AddSeconds(.3)).FerryArrival);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void AVerifiedCrossingPreservesTheOldGoalForArrivalReplanDetection(bool changedGoal)
    {
        var tracker=new NavigationJourneyTracker();
        GameDataSnapshot Data(string session,RouteSnapshot route)=>new(Pose(0,0),route,RouteAvailability.Available,session,true);
        var original=RouteSnapshot.Parse([new(1,2000,100),new(2,1000,50),new(3,0,0)],Now);
        Assert.Equal(NavigationJourneyNoticeKind.Start,tracker.Update(Data("old",original),Now).Notice!.Kind);
        Assert.True(tracker.ResumeAfterFerry("new"));
        var after=RouteSnapshot.Parse([new(2,1000,50),new(changedGoal?4UL:3UL,0,0)],Now);
        var result=tracker.Update(Data("new",after),Now);
        if(changedGoal)Assert.Equal(NavigationJourneyNoticeKind.Replanned,result.Notice!.Kind);
        else Assert.Null(result.Notice);
    }

    [Fact]
    public void TheRealHostObserveOrderPreservesOnlyVerifiedFerryJourneys()
    {
        var store=Store();var coordinator=new GuidanceCoordinator{JourneyEstimateProvider=_=>new(1000,60,null)};
        var spoken=new List<QueuedGuidanceSpeech>();coordinator.Queued+=spoken.Add;
        var initial=store.Capture(Now);coordinator.Observe(initial,Now);
        Assert.Single(spoken,p=>p.Text.StartsWith("准备出发"));
        store.UpdateVehicle(Pose(0,.1,2),Raw(true,"a","b"));store.UpdateVehicle(Pose(0,.2,3),Raw(true,"a","b"));
        store.UpdateVehicle(Pose(10000,.3,4),Raw(true,"a","b"));var arrived=store.Capture(Now.AddSeconds(.3));
        coordinator.Observe(arrived,Now.AddSeconds(.3),processJourney:false); // NavPlugin's first, early call.
        Assert.True(coordinator.HasVerifiedFerryContinuation(arrived));
        if(!coordinator.HasVerifiedFerryContinuation(arrived))coordinator.Reset(); // Session branch in NavPlugin.
        coordinator.Observe(arrived,Now.AddSeconds(.3));
        Assert.Single(spoken,p=>p.Text.StartsWith("准备出发"));
        store.UpdateVehicle(Pose(15000,.4,5),Raw(true,"a","b"));var teleported=store.Capture(Now.AddSeconds(.4));
        store.UpdateRoute(RouteSnapshot.Parse([new(301,1000,10),new(999,0,0)],Now.AddSeconds(.4)));
        teleported=store.Capture(Now.AddSeconds(.4));coordinator.Observe(teleported,Now.AddSeconds(.4),processJourney:false);
        Assert.False(coordinator.HasVerifiedFerryContinuation(teleported));coordinator.Reset();coordinator.Observe(teleported,Now.AddSeconds(.4));
        Assert.Equal(2,spoken.Count(p=>p.Text.StartsWith("准备出发")));
    }
}
