using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationRouteStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-19T00:00:00Z");
    private static RouteSnapshot Route(params ulong[] uids) => RouteSnapshot.Parse(
        uids.Select((uid, i) => new RouteNode(uid, (uids.Length - i) * 100, (uids.Length - i) * 10)), Now);

    [Fact]
    public void ExactSuffixMovesPointerWithoutReplacingFullRouteOrBroadcasting()
    {
        var store = new GameSnapshotStore(); var changes = new List<NavigationRouteState>();
        store.RouteChanged += changes.Add;
        var original = Route(1, 2, 1, 3); store.UpdateRoute(original);
        var first = store.Capture(Now).NavigationRoute;
        Assert.Equal(1, first.Generation);
        var suffix = Route(1, 3) with { ReceivedAt = Now.AddSeconds(1) };
        store.UpdateRoute(suffix);
        var next = store.Capture(Now.AddSeconds(1));
        Assert.Single(changes); Assert.Same(original, next.NavigationRoute.FullRoute);
        Assert.Same(suffix, next.Route); Assert.Same(next.Route, next.NavigationRoute.CurrentSnapshot);
        Assert.Equal(2, next.NavigationRoute.CurrentNodeIndex);
        Assert.Equal(0, first.CurrentNodeIndex); Assert.Equal(4, first.FullRoute.Nodes.Count);
        Assert.Equal(first.Generation, next.NavigationRoute.Generation);
    }

    [Fact]
    public void EqualUidsRefreshMetricsIndependentlyOfStableGeometry()
    {
        var store = new GameSnapshotStore(); int changed = 0; store.RouteChanged += _ => changed++;
        var original = Route(1, 2); store.UpdateRoute(original);
        var current = RouteSnapshot.Parse([new(1, 900, 99), new(2, 4, 2)], Now.AddSeconds(1));
        store.UpdateRoute(current); var snapshot = store.Capture(Now.AddSeconds(1));
        Assert.Equal(1, changed); Assert.Equal(0, snapshot.NavigationRoute.CurrentNodeIndex);
        Assert.Equal(200, snapshot.NavigationRoute.FullRoute.Nodes[0].DistanceToEndMeters);
        Assert.Equal(900, snapshot.Route.Nodes[0].DistanceToEndMeters);
        Assert.Equal(99, snapshot.Route.Nodes[0].TimeToEndSeconds);
    }

    [Theory]
    [InlineData(new ulong[] { 2, 3, 4 })] // Prefix trimmed and tail appended: not a suffix.
    [InlineData(new ulong[] { 1, 9, 3 })] // Same count and destination do not imply continuity.
    [InlineData(new ulong[] { 1, 2 })] // Tail removed.
    public void NonSuffixReplacesRouteAndBroadcasts(ulong[] newUids)
    {
        var store = new GameSnapshotStore(); var changes = new List<NavigationRouteState>(); store.RouteChanged += changes.Add;
        store.UpdateRoute(Route(1, 2, 3)); var next = Route(newUids); store.UpdateRoute(next);
        Assert.Equal(2, changes.Count); Assert.Equal(2, changes[1].Generation);
        Assert.Same(next, changes[1].FullRoute); Assert.Equal(0, changes[1].CurrentNodeIndex);
    }

    [Fact]
    public void PointerCannotMoveBackwardWithinGeneration()
    {
        var state = NavigationRouteState.Empty.Accept(Route(1, 2, 3)).Accept(Route(3));
        var expanded = state.Accept(Route(2, 3));
        Assert.Equal(state.Generation + 1, expanded.Generation); Assert.Equal(0, expanded.CurrentNodeIndex);
    }

    [Fact]
    public void EmptyClearsOnceAndInvalidInputDoesNotPretendToClear()
    {
        var store = new GameSnapshotStore(); var changes = new List<NavigationRouteState>(); store.RouteChanged += changes.Add;
        store.UpdateRoute(Route()); Assert.Empty(changes);
        store.UpdateRoute(Route(1, 2));
        var invalid = RouteSnapshot.Parse([new(1, -1, 0)], Now);
        store.UpdateRoute(invalid);
        Assert.Single(changes); Assert.Equal(2, store.Capture(Now).NavigationRoute.FullRoute.Nodes.Count);
        Assert.Same(invalid, store.Capture(Now).Route);
        store.UpdateRoute(Route(2)); Assert.Single(changes);
        store.UpdateRoute(Route()); store.UpdateRoute(Route());
        Assert.Equal(2, changes.Count); Assert.Empty(changes[1].FullRoute.Nodes); Assert.Equal(2, changes[1].Generation);
        store.UpdateRoute(Route(2)); Assert.Equal(3, changes.Count); Assert.Equal(3, changes[2].Generation);
    }

    [Fact]
    public void SessionResetDoesNotKeepPreviousCompleteRoute()
    {
        var store = new GameSnapshotStore();
        var pose = new VehicleSnapshot(0, 0, 0, 0, 20, 1000, true, false, 100, Now);
        store.UpdateVehicle(pose); store.UpdateRoute(Route(1, 2));
        var previous = store.Capture(Now);
        store.UpdateVehicle(pose with { X = 1000, SimulationTime = 101 });
        var next = store.Capture(Now);
        Assert.NotEqual(previous.SessionId, next.SessionId);
        Assert.Empty(next.NavigationRoute.FullRoute.Nodes);
        Assert.Equal(0, next.NavigationRoute.CurrentNodeIndex);
        Assert.Equal(RouteAvailability.Unknown, next.Route.Availability);
        Assert.True(next.NavigationRoute.Generation > previous.NavigationRoute.Generation);
    }

    [Fact]
    public void CallbackRunsAfterUnlockAndObserverErrorsAreNotSwallowed()
    {
        var store = new GameSnapshotStore();
        store.RouteChanged += expected =>
        {
            var read = Task.Run(() => store.Capture(Now));
            Assert.True(read.Wait(TimeSpan.FromSeconds(3)));
            Assert.Same(expected, read.Result.NavigationRoute);
            throw new InvalidOperationException("observer failure");
        };
        var error = Assert.Throws<InvalidOperationException>(() => store.UpdateRoute(Route(1)));
        Assert.Equal("observer failure", error.Message);
        Assert.Single(store.Capture(Now).Route.Nodes);
    }

    [Fact]
    public async Task ConcurrentCaptureNeverSeparatesPointerFromItsSnapshot()
    {
        var store = new GameSnapshotStore();
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            { store.UpdateRoute(Route(1, 2, 3)); store.UpdateRoute(Route(2, 3)); store.UpdateRoute(Route(3)); }
        });
        for (int i = 0; i < 600; i++)
        {
            var snapshot = store.Capture(Now); var state = snapshot.NavigationRoute;
            Assert.Same(snapshot.Route, state.CurrentSnapshot);
            Assert.Equal(state.FullRoute.Nodes.Count - state.CurrentNodeIndex, snapshot.Route.Nodes.Count);
            Assert.Equal(state.FullRoute.Nodes.Skip(state.CurrentNodeIndex).Select(n => n.Uid), snapshot.Route.Nodes.Select(n => n.Uid));
        }
        await producer;
    }
}
