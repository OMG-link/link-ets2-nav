using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class FineObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
    private static VehicleSnapshot Vehicle() => new(123, 4, 567, 0.25, 20, 1000, true, false, 100, Now);

    [Fact]
    public void RisingFineCapturesObservedPoseButDoesNotInferCameraIdentity()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(false, "", 0, Now);
        store.ObserveFine(true, "speeding_camera", 550, Now);
        var fine = Assert.IsType<FineObservation>(store.Capture(Now).LatestFine);
        Assert.True(fine.IsSpeedCameraOffence);
        Assert.Equal(550UL, fine.Amount);
        Assert.Equal(123, fine.Vehicle.X);
        Assert.Equal(Now, fine.ObservedAt);
    }

    [Fact]
    public void InitialLatchedFineIsIgnoredAndRepeatedTrueFlagDoesNotRecapture()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(true, "speeding_camera", 550, Now);
        store.ObserveFine(true, "speeding_camera", 550, Now.AddMilliseconds(100));
        Assert.Null(store.Capture(Now).LatestFine);
        store.ObserveFine(false, "speeding_camera", 550, Now);
        store.ObserveFine(true, "speeding_camera", 550, Now);
        var first = store.Capture(Now).LatestFine;
        store.ObserveFine(true, "speeding_camera", 550, Now.AddMilliseconds(100));
        Assert.Same(first, store.Capture(Now).LatestFine);
    }

    [Fact]
    public void PoliceSpeedingRemainsDistinctFromCameraSpeeding()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(false, "", 0, Now);
        store.ObserveFine(true, "speeding", 550, Now);
        Assert.False(store.Capture(Now).LatestFine!.IsSpeedCameraOffence);
    }

    [Fact]
    public void TeleportClearsFineAndRequiresNewEventBaseline()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(false, "", 0, Now);
        store.ObserveFine(true, "speeding_camera", 550, Now);
        store.UpdateVehicle(Vehicle() with { X = 10000 });
        store.ObserveFine(true, "speeding_camera", 550, Now);
        Assert.Null(store.Capture(Now).LatestFine);
    }

    [Fact]
    public void FrozenVehicleCannotProduceNewFineObservation()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(false, "", 0, Now);
        store.ObserveFine(true, "speeding_camera", 550, Now.AddSeconds(3));
        Assert.Null(store.Capture(Now.AddSeconds(3)).LatestFine);
    }
}
