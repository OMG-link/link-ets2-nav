using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class RuntimeSnapshotTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
    private static VehicleSnapshot Vehicle(double x = 0, ulong time = 100, bool active = true, bool paused = false)
        => new(x, 0, 0, 0, 20, 1000, active, paused, time, Now);

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(0.25, -1, 0)]
    [InlineData(0.5, 0, 1)]
    [InlineData(0.75, 1, 0)]
    public void HeadingTurnsFollowScsNorthWestSouthEast(double heading, double x, double z)
    {
        var forward = (Vehicle() with { HeadingTurns = heading }).ForwardDirection;
        Assert.Equal(x, forward.X, 5);
        Assert.Equal(z, forward.Z, 5);
    }

    [Fact]
    public void MissingRouteIsUnknownWhileFreshEmptyRouteIsNone()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        Assert.Equal(RouteAvailability.Unknown, store.Capture(Now).RouteAvailability);
        store.UpdateVehicle(Vehicle() with { NavigationDistanceRemainingMeters = 0 });
        store.UpdateRoute(RouteSnapshot.Parse([new(0, 0, 0)], Now));
        Assert.Equal(RouteAvailability.None, store.Capture(Now).RouteAvailability);
        Assert.Equal(RouteAvailability.Stale, store.Capture(Now.AddSeconds(4)).RouteAvailability);
    }

    [Fact]
    public void EmptyRouteWithPositiveTelemetryDistanceIsInvalidRatherThanNoRoute()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.UpdateRoute(RouteSnapshot.Parse([new(0, 0, 0)], Now));
        Assert.Equal(RouteAvailability.Invalid, store.Capture(Now).RouteAvailability);
    }

    [Fact]
    public void StaleRouteCannotRemainAvailableAfterNavigationIsCleared()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle() with { NavigationDistanceRemainingMeters = 0 });
        store.UpdateRoute(RouteSnapshot.Parse([new(7, 1000, 30)], Now));
        Assert.Equal(RouteAvailability.Invalid, store.Capture(Now).RouteAvailability);
    }

    [Fact]
    public void RouteCopiesInputAndRevisionIgnoresDistancesButPreservesOrdering()
    {
        var input = new[] { new RouteNode(7, 500, 30), new RouteNode(8, 0, 0), new RouteNode(0, 0, 0) };
        var first = RouteSnapshot.Parse(input, Now);
        input[0] = new(99, 500, 30);
        Assert.Equal(7UL, first.Nodes[0].Uid);
        Assert.Equal(2, first.Nodes.Count);
        Assert.Equal(first.Revision, RouteSnapshot.Parse([new(7, 600, 40), new(8, 0, 0)], Now).Revision);
        Assert.NotEqual(first.Revision, RouteSnapshot.Parse([new(8, 600, 40), new(7, 0, 0)], Now).Revision);
    }

    [Fact]
    public void RepeatedRouteNodeIsKeptForRoutesThatLoop()
    {
        var route = RouteSnapshot.Parse([new(7, 600, 40), new(8, 500, 30), new(7, 0, 0)], Now);
        Assert.Equal(RouteAvailability.Available, route.Availability);
        Assert.Equal(3, route.Nodes.Count);
    }

    [Fact]
    public void FullSharedMemoryPayloadIsMarkedPotentiallyTruncated()
    {
        var route = RouteSnapshot.Parse(Enumerable.Range(1, 6000).Select(i => new RouteNode((ulong)i, 0, 0)), Now);
        Assert.True(route.MayBeTruncated);
    }

    [Theory]
    [InlineData(float.NaN, 1)]
    [InlineData(float.PositiveInfinity, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidRouteMetricsRejectWholeSnapshot(float distance, float time)
        => Assert.Equal(RouteAvailability.Invalid, RouteSnapshot.Parse([new(7, distance, time)], Now).Availability);

    [Fact]
    public void NonzeroNodeAfterPaddingRejectsTornRoute()
        => Assert.Equal(RouteAvailability.Invalid,
            RouteSnapshot.Parse([new(7, 500, 30), new(0, 0, 0), new(8, 0, 0)], Now).Availability);

    [Fact]
    public void VehicleStalenessPauseAndInvalidPositionBlockUse()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        Assert.True(store.Capture(Now).IsVehicleUsable);
        Assert.False(store.Capture(Now.AddSeconds(3)).IsVehicleUsable);
        Assert.False(store.Capture(Now.AddSeconds(-1)).IsVehicleUsable);
        store.UpdateVehicle(Vehicle(paused: true));
        Assert.False(store.Capture(Now).IsVehicleUsable);
        store.UpdateVehicle(Vehicle(x: double.NaN));
        Assert.False(store.Capture(Now).IsVehicleUsable);
    }

    [Fact]
    public void FrozenSharedMemoryDoesNotStayFreshThroughRepeatedHostPolls()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        for (int second = 1; second <= 3; second++)
            store.UpdateVehicle(Vehicle() with { ReceivedAt = Now.AddSeconds(second) });
        Assert.False(store.Capture(Now.AddSeconds(3)).IsVehicleUsable);
        store.UpdateVehicle(Vehicle(time: 101) with { ReceivedAt = Now.AddSeconds(4) });
        Assert.True(store.Capture(Now.AddSeconds(4)).IsVehicleUsable);
    }

    [Fact]
    public void PauseResumeTransitionResetsProgressAgeEvenBeforeFirstSimulationTick()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.UpdateVehicle(Vehicle(paused: true) with { ReceivedAt = Now.AddSeconds(10) });
        Assert.False(store.Capture(Now.AddSeconds(10)).IsVehicleUsable);
        store.UpdateVehicle(Vehicle() with { ReceivedAt = Now.AddSeconds(11) });
        Assert.True(store.Capture(Now.AddSeconds(11)).IsVehicleUsable);
    }

    [Theory]
    [InlineData(1000, 101, true)]
    [InlineData(0, 99, true)]
    [InlineData(0, 101, false)]
    public void DiscontinuityStartsNewSessionAndInvalidatesRoute(double x, ulong time, bool active)
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.UpdateRoute(RouteSnapshot.Parse([new(7, 500, 30)], Now));
        var before = store.Capture(Now);
        store.UpdateVehicle(Vehicle(x, time, active));
        var after = store.Capture(Now);
        Assert.NotEqual(before.SessionId, after.SessionId);
        Assert.Equal(RouteAvailability.Unknown, after.RouteAvailability);
    }

    [Fact]
    public void NormalMovementPreservesSessionAndRoute()
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle());
        store.UpdateRoute(RouteSnapshot.Parse([new(7, 500, 30)], Now));
        var before = store.Capture(Now);
        store.UpdateVehicle(Vehicle(20, 101) with { ReceivedAt = Now.AddSeconds(1) });
        var after = store.Capture(Now.AddSeconds(1));
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(RouteAvailability.Available, after.RouteAvailability);
    }
}
