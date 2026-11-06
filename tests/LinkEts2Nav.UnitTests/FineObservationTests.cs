using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class FineObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
    private static VehicleSnapshot Vehicle() => new(123, 4, 567, 0.25, 20, 1000, true, false, 100, Now);
    private static GameSnapshotStore Store(bool flag = false, string offence = "", ulong amount = 0)
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.ObserveFine(flag, offence, amount, Now);
        return store;
    }
    private static void StableFine(GameSnapshotStore store, bool flag, string offence, ulong amount, DateTimeOffset? time = null)
    {
        var observed = time ?? Now;
        store.ObserveFine(flag, offence, amount, observed);
        store.ObserveFine(flag, offence, amount, observed.AddMilliseconds(10));
    }

    [Fact]
    public void BothToggleEdgesCaptureRepeatedSameFinePayload()
    {
        var store = Store(false, "speeding_camera", 550);
        StableFine(store, true, "speeding_camera", 550);
        StableFine(store, false, "speeding_camera", 550, Now.AddMilliseconds(30));
        var fines = store.DrainFines();
        Assert.Equal(2, fines.Count);
        Assert.True(fines[0].EventFlag);
        Assert.False(fines[1].EventFlag);
        Assert.All(fines, fine => Assert.True(fine.IsSignalCertain));
        Assert.Equal(new long[] { 1, 2 }, fines.Select(f => f.Sequence));
    }

    [Fact]
    public void InitialLatchedFineAndRepeatedUnchangedSamplesAreIgnored()
    {
        var store = Store(true, "speeding_camera", 550);
        StableFine(store, true, "speeding_camera", 550);
        Assert.Null(store.Capture(Now).LatestFine);
        Assert.Empty(store.DrainFines());
    }

    [Fact]
    public void NewFineWaitsForFollowingStableSample()
    {
        var store = Store();
        store.ObserveFine(true, "speeding_camera", 550, Now);
        Assert.Null(store.Capture(Now).LatestFine);
        store.ObserveFine(true, "speeding_camera", 550, Now.AddMilliseconds(10));
        var fine = Assert.Single(store.DrainFines());
        Assert.Equal(550UL, fine.Amount);
        Assert.Equal(123, fine.Vehicle.X);
        Assert.Equal(Now, fine.ObservedAt);
    }

    [Fact]
    public void ToggleBeforePayloadKeepsFirstPoseAndCommitsFinalStablePayload()
    {
        var store = Store(false, "avoid_sleeping", 150);
        var firstSeen = Now.AddMilliseconds(10);
        store.ObserveFine(true, "avoid_sleeping", 150, firstSeen);
        store.UpdateVehicle(Vehicle() with { X = 130, SimulationTime = 101, ReceivedAt = Now.AddMilliseconds(20) });
        store.ObserveFine(true, "speeding_camera", 550, Now.AddMilliseconds(20));
        Assert.Empty(store.DrainFines());
        store.ObserveFine(true, "speeding_camera", 550, Now.AddMilliseconds(30));
        var fine = Assert.Single(store.DrainFines());
        Assert.Equal("speeding_camera", fine.Offence);
        Assert.Equal(550UL, fine.Amount);
        Assert.Equal(123, fine.Vehicle.X);
        Assert.Equal(firstSeen, fine.ObservedAt);
        Assert.True(fine.IsSignalCertain);
    }

    [Fact]
    public void PayloadChangeWithoutToggleIsRetainedAsUncertainEvidence()
    {
        var store = Store(false, "avoid_sleeping", 150);
        StableFine(store, false, "speeding_camera", 550);
        var fine = Assert.Single(store.DrainFines());
        Assert.True(fine.IsSpeedCameraOffence);
        Assert.False(fine.IsSignalCertain);
        Assert.Equal(FineObservationSignal.PayloadChange, fine.Signal);
    }

    [Fact]
    public void LaterFatigueFineDoesNotEraseCameraEvidenceOrUndrainedEvent()
    {
        var store = Store();
        StableFine(store, true, "speeding_camera", 550);
        StableFine(store, false, "avoid_sleeping", 150, Now.AddMilliseconds(30));
        var snapshot = store.Capture(Now.AddMilliseconds(50));
        Assert.Equal("avoid_sleeping", snapshot.LatestFine!.Offence);
        Assert.Equal("speeding_camera", snapshot.LatestCameraFine!.Offence);
        Assert.Equal(2, snapshot.FineHistory!.Count);
        Assert.Equal(2, store.DrainFines().Count);
        Assert.Empty(store.DrainFines());
        Assert.Equal(2, store.Capture(Now.AddMilliseconds(50)).FineHistory!.Count);
    }

    [Fact]
    public void OrdinarySpeedingRemainsDistinctFromCameraSpeeding()
    {
        var store = Store();
        StableFine(store, true, "speeding", 550);
        Assert.False(store.Capture(Now).LatestFine!.IsSpeedCameraOffence);
        Assert.Null(store.Capture(Now).LatestCameraFine);
    }

    [Fact]
    public void TeleportClearsDisplayedHistoryButCommittedEventsRemainDrainable()
    {
        var store = Store();
        StableFine(store, true, "speeding_camera", 550);
        store.UpdateVehicle(Vehicle() with { X = 10000 });
        store.ObserveFine(true, "speeding_camera", 550, Now);
        StableFine(store, true, "speeding_camera", 550);
        var snapshot = store.Capture(Now);
        Assert.Null(snapshot.LatestFine);
        Assert.Null(snapshot.LatestCameraFine);
        Assert.Empty(snapshot.FineHistory!);
        Assert.Single(store.DrainFines());
    }

    [Fact]
    public void PendingUnconfirmedEventDoesNotSurviveTeleport()
    {
        var store = Store();
        store.ObserveFine(true, "speeding_camera", 550, Now);
        store.UpdateVehicle(Vehicle() with { X = 10000 });
        StableFine(store, true, "speeding_camera", 550);
        Assert.Empty(store.DrainFines());
    }

    [Fact]
    public void FrozenVehicleCannotProduceNewFineObservation()
    {
        var store = Store();
        StableFine(store, true, "speeding_camera", 550, Now.AddSeconds(3));
        Assert.Null(store.Capture(Now.AddSeconds(3)).LatestFine);
        Assert.Empty(store.DrainFines());
    }

    [Fact]
    public void FineHistoryAndUndrainedQueueAreBoundedWithOverflowCount()
    {
        var store = Store(false, "speeding_camera", 550);
        for (int i = 1; i <= 70; i++)
            StableFine(store, i % 2 != 0, "speeding_camera", 550, Now.AddMilliseconds(i * 20));
        var snapshot = store.Capture(Now.AddMilliseconds(1500));
        Assert.Equal(64, snapshot.FineHistory!.Count);
        Assert.Equal(6, snapshot.DroppedFineCount);
        var fines = store.DrainFines();
        Assert.Equal(64, fines.Count);
        Assert.Equal(7, fines[0].Sequence);
        Assert.Equal(70, fines[^1].Sequence);
    }

    [Fact]
    public void HistorySnapshotsAreImmutableCopies()
    {
        var store = Store();
        StableFine(store, true, "speeding_camera", 550);
        var first = store.Capture(Now).FineHistory!;
        StableFine(store, false, "avoid_sleeping", 150);
        Assert.Single(first);
        Assert.Equal("speeding_camera", first[0].Offence);
    }

    [Fact]
    public void SecondToggleBeforeConfirmationDoesNotInventFirstPayload()
    {
        var store = Store();
        store.ObserveFine(true, "speeding_camera", 550, Now);
        StableFine(store, false, "avoid_sleeping", 150);
        var fine = Assert.Single(store.DrainFines());
        Assert.Equal("avoid_sleeping", fine.Offence);
        Assert.False(fine.EventFlag);
    }
}
