using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class PositionSignalTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-14T05:35:39Z");
    private static VehicleSnapshot Pose(double seconds=0,ulong tick=100,bool paused=false,double x=0)
        =>new(x,0,0,0,17,10000,true,paused,tick,Now.AddSeconds(seconds));

    [Fact]
    public void NormalMovementAfterFiveSecondGapPreservesRouteSessionAndJourney()
    {
        var store=new GameSnapshotStore();store.UpdateVehicle(Pose());
        var route=RouteSnapshot.Parse([new(1,1000,60),new(2,0,0)],Now);store.UpdateRoute(route);
        var first=store.Capture(Now);var journey=new NavigationJourneyTracker();
        Assert.Equal(NavigationJourneyNoticeKind.Start,journey.Update(first,Now).Notice!.Kind);
        Assert.True(store.Capture(Now.AddSeconds(5)).PositionSignal.IsWeak);
        store.UpdateVehicle(Pose(5.856,101,x:100.6));
        var restored=store.Capture(Now.AddSeconds(5.856));
        Assert.Equal(first.SessionId,restored.SessionId);Assert.Same(route,restored.Route);
        Assert.False(restored.PositionSignal.IsWeak);
        store.UpdateRoute(route with{ReceivedAt=Now.AddSeconds(5.856)});
        Assert.Null(journey.Update(store.Capture(Now.AddSeconds(5.856)),Now.AddSeconds(5.856)).Notice);
    }

    [Fact]
    public void TelemetryFreezeWarnsOnceAndProgressRecoveryRearmsIt()
    {
        var store=new GameSnapshotStore();store.UpdateVehicle(Pose());
        for(int second=1;second<=4;second++)store.UpdateVehicle(Pose(second));
        Assert.False(store.Capture(Now.AddSeconds(4.99)).PositionSignal.IsWeak);
        for(int second=5;second<=6;second++)store.UpdateVehicle(Pose(second));
        var weak=store.Capture(Now.AddSeconds(6));
        Assert.False(weak.IsVehicleUsable);Assert.True(weak.PositionSignal.IsWeak);
        Assert.Equal(weak.PositionSignal.Episode,store.Capture(Now.AddSeconds(8)).PositionSignal.Episode);
        store.UpdateVehicle(Pose(8,101));
        Assert.False(store.Capture(Now.AddSeconds(8)).PositionSignal.IsWeak);
        Assert.Equal(weak.PositionSignal.Episode+1,store.Capture(Now.AddSeconds(13)).PositionSignal.Episode);
    }

    [Fact]
    public void KnownPauseFreezesAccumulatedTimeAndDoesNotMakeOldPoseFresh()
    {
        var store=new GameSnapshotStore();store.UpdateVehicle(Pose());
        store.Capture(Now.AddSeconds(3));
        store.UpdateVehicle(Pose(3,paused:true));
        Assert.False(store.Capture(Now.AddSeconds(103)).PositionSignal.IsWeak);
        store.UpdateVehicle(Pose(103));
        Assert.False(store.Capture(Now.AddSeconds(104)).PositionSignal.IsWeak);
        var expired=store.Capture(Now.AddSeconds(105));
        Assert.True(expired.PositionSignal.IsWeak);
        Assert.False(store.Capture(Now.AddSeconds(106)).IsVehicleUsable);
    }

    [Fact]
    public void NormalStationaryTelemetryAndInactiveSdkNeverWarn()
    {
        var store=new GameSnapshotStore();
        for(int second=0;second<=20;second++)
        {
            store.UpdateVehicle(Pose(second,(ulong)(100+second)) with{SpeedMetersPerSecond=0});
            Assert.False(store.Capture(Now.AddSeconds(second)).PositionSignal.IsWeak);
        }
        store.UpdateVehicle(Pose(21,121) with{SdkActive=false});
        Assert.False(store.Capture(Now.AddSeconds(100)).PositionSignal.IsWeak);
    }

    [Fact]
    public void SignalNoticeIsIndependentOfRouteAndCanBeWithdrawnWhenRecovered()
    {
        var store=new GameSnapshotStore();store.UpdateVehicle(Pose());
        var guidance=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();
        guidance.Queued+=queued.Add;
        guidance.Observe(store.Capture(Now.AddSeconds(5)),Now.AddSeconds(5),false);
        guidance.Observe(store.Capture(Now.AddSeconds(6)),Now.AddSeconds(6),false);
        Assert.Equal(PositionSignalTracker.Text,Assert.Single(queued).Text);
        store.UpdateVehicle(Pose(7,101));guidance.Observe(store.Capture(Now.AddSeconds(7)),Now.AddSeconds(7),false);
        guidance.Observe(store.Capture(Now.AddSeconds(12)),Now.AddSeconds(12),false);
        Assert.Equal(2,queued.Count);
    }
}
