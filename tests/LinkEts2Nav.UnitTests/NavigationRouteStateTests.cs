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
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SuffixCutInsideAnExactDuplicateBlockUsesIngressIndices(int hostCut)
    {
        RouteNode[] host=[new(1,300,30),new(2,200,20),new(2,200,20),new(3,0,0)];
        var full=RouteSnapshot.Parse(host,Now);var store=new GameSnapshotStore();int changes=0;
        store.RouteChanged+=_=>changes++;store.UpdateRoute(full);
        store.UpdateRoute(RouteSnapshot.Parse(host.Skip(hostCut),Now.AddSeconds(1)));
        var current=store.Capture(Now.AddSeconds(1)).NavigationRoute;
        Assert.Equal(3,full.Nodes.Count);Assert.Equal(1,current.CurrentNodeIndex);
        Assert.Same(full,current.FullRoute);Assert.Equal(1,current.Generation);Assert.Equal(1,changes);
    }

    [Fact]
    public void RollingWindowKeepsVisitOrdinalsButRebuildsTheUnseenGeometry()
    {
        var original = NavigationRouteState.Empty.Accept(Route(1, 2, 3));
        var next = Route(2, 3, 4);
        var extended = original.Accept(next);
        Assert.Equal(original.ContinuityGeneration, extended.ContinuityGeneration);
        Assert.Equal(original.GeometryGeneration + 1, extended.GeometryGeneration);
        Assert.Same(next, extended.FullRoute);
        Assert.Equal(1, extended.FirstNodeOrdinal);
        Assert.Equal(0, extended.CurrentNodeIndex);
        var trimmed = extended.Accept(Route(3, 4));
        Assert.Equal(2, trimmed.FirstNodeOrdinal + trimmed.CurrentNodeIndex);
        Assert.Equal(extended.GeometryGeneration, trimmed.GeometryGeneration);
        Assert.False(LinkEts2Nav.Map.LoadedNavigationRoute.Covers(original.FullRoute, next));
        Assert.True(LinkEts2Nav.Map.LoadedNavigationRoute.Covers(next, trimmed.CurrentSnapshot));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(9, true)]
    public void RestoredWindowIsComparedWithThePreviouslyKnownTail(int tail, bool replaced)
    {
        var original = NavigationRouteState.Empty.Accept(Route(1, 2, 3, 4));
        var partial = original.Accept(Route(2, 3) with { MayBeTruncated = true });
        var missing = partial.Accept(RouteSnapshot.Parse([new(8, -1, 0)], Now));
        Assert.Same(partial.FullRoute, missing.FullRoute);
        var restored = missing.Accept(Route(2, 3, (ulong)tail));
        Assert.Equal(replaced, original.ContinuityGeneration != restored.ContinuityGeneration);
        Assert.Equal(replaced ? 0 : 1, restored.FirstNodeOrdinal);
    }

    [Fact]
    public void SingleRemainingVisibleNodeCanRecoverThePreviouslyProvenNextEdge()
    {
        var original = NavigationRouteState.Empty.Accept(Route(1, 2, 3, 4));
        var partial = original.Accept(Route(2, 3)).Accept(Route(3));
        Assert.Equal(original.ContinuityGeneration, partial.ContinuityGeneration);
        var next = partial.Accept(Route(3, 4, 5));
        Assert.Equal(original.ContinuityGeneration, next.ContinuityGeneration);
        Assert.Equal(2, next.FirstNodeOrdinal);
        Assert.NotEqual(original.ContinuityGeneration, partial.Accept(Route(4)).ContinuityGeneration);
    }

    [Fact]
    public void SingleJunctionOrAmbiguousRepeatedVisitsCannotProveContinuation()
    {
        var crossing = NavigationRouteState.Empty.Accept(Route(1, 2, 3));
        Assert.NotEqual(crossing.ContinuityGeneration, crossing.Accept(Route(3, 4)).ContinuityGeneration);
        var loop = NavigationRouteState.Empty.Accept(Route(1, 2, 1, 2, 1, 2));
        Assert.NotEqual(loop.ContinuityGeneration, loop.Accept(Route(1, 2, 1, 2, 3)).ContinuityGeneration);
    }

    [Fact]
    public void ConfirmedNearerGoalChangesJourneyDespiteSharedPrefix()
    {
        RouteSnapshot Complete(params ulong[] ids) => RouteSnapshot.Parse(ids.Select((id, i) =>
            new RouteNode(id, (ids.Length - i - 1) * 100, 0)), Now);
        var original = NavigationRouteState.Empty.Accept(Complete(1, 2, 3, 4));
        var next = original.Accept(Complete(2, 3));
        Assert.NotEqual(original.ContinuityGeneration, next.ContinuityGeneration);
        Assert.Equal(0, next.FirstNodeOrdinal);
    }

    [Fact]
    public void EmptyMmapWhileDrivingPreservesRouteEvenWhenVehicleCallbackIsNewer()
    {
        var store = new GameSnapshotStore();
        var pose = new VehicleSnapshot(0, 0, 0, 0, 12, 900, true, false, 1, Now.AddMilliseconds(40));
        store.UpdateVehicle(pose);
        var route = Route(1, 2, 3);
        store.UpdateRoute(route);
        var initial = store.Capture(pose.ReceivedAt).NavigationRoute;
        var empty = Route() with { ReceivedAt = Now.AddMilliseconds(20) };
        store.UpdateRoute(empty);
        var missing = store.Capture(pose.ReceivedAt);
        Assert.Equal(RouteAvailability.Invalid, missing.RouteAvailability);
        Assert.Same(empty, missing.Route);
        Assert.Same(route, missing.NavigationRoute.FullRoute);
        store.UpdateVehicle(pose with { Paused = true });
        Assert.Equal(initial.ContinuityGeneration, store.Capture(pose.ReceivedAt).NavigationRoute.ContinuityGeneration);
        store.UpdateVehicle(pose);
        store.UpdateRoute(Route(2, 3));
        var resumed = store.Capture(pose.ReceivedAt).NavigationRoute;
        Assert.Equal(initial.GeometryGeneration, resumed.GeometryGeneration);
        Assert.Equal(initial.ContinuityGeneration, resumed.ContinuityGeneration);
    }

    [Fact]
    public void ConfirmedZeroClearsOnceDespiteHeldNodesAndPauseThenStartsNewRoute()
    {
        var store = new GameSnapshotStore();
        var pose = new VehicleSnapshot(0, 0, 0, 0, 12, 900, true, false, 1, Now);
        var raw = Route(1, 2, 3);
        store.UpdateVehicle(pose); store.UpdateRoute(raw);
        var initial = store.Capture(Now).NavigationRoute;
        GameDataSnapshot Step(int ms, double remaining, bool paused = false)
        {
            var time = Now.AddMilliseconds(ms);
            store.UpdateVehicle(pose with { SimulationTime = (ulong)ms + 1, ReceivedAt = time,
                NavigationDistanceRemainingMeters = remaining, Paused = paused });
            return store.Capture(time);
        }
        Step(100, 0); Step(600, 0);
        var ended = Step(1100, 0);
        Assert.Same(raw, ended.Route);
        Assert.Equal(RouteAvailability.None, ended.RouteAvailability);
        Assert.Empty(ended.NavigationRoute.FullRoute.Nodes);
        Assert.Equal(initial.GeometryGeneration + 1, ended.NavigationRoute.GeometryGeneration);
        Assert.Equal(ended.NavigationRoute.GeometryGeneration, Step(1300, 0, true).NavigationRoute.GeometryGeneration);
        Assert.Equal(ended.NavigationRoute.ContinuityGeneration + 1, Step(1500, 900).NavigationRoute.ContinuityGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumersThatSkippedTheReplacementStillReactOnce(bool returnsToOriginal)
    {
        var store = new GameSnapshotStore();
        store.UpdateVehicle(new(0,0,0,0,12,900,true,false,1,Now));
        store.UpdateRoute(Route(1, 2, 3));
        var initial = store.Capture(Now);
        var guidance = new GuidanceCoordinator();
        var journey = new NavigationJourneyTracker();
        Assert.Equal(NavigationJourneyNoticeKind.Start, journey.Update(initial, Now).Notice!.Kind);
        guidance.Observe(initial, Now);
        guidance.Publish(new("navigation", initial.SessionId, initial.Route.Revision, 0, true, Now, []));

        store.UpdateRoute(Route(1, 9, 3));
        store.UpdateRoute(returnsToOriginal ? Route(1, 2, 3) : Route(9, 3));
        var later = store.Capture(Now);
        guidance.Observe(later, Now);
        Assert.True(guidance.WillReplaceSource("navigation", later.Route.Revision));
        Assert.Equal(NavigationJourneyNoticeKind.Replanned, journey.Update(later, Now).Notice!.Kind);
        guidance.Publish(new("navigation", later.SessionId, later.Route.Revision, 0, true, Now, []));
        Assert.False(guidance.WillReplaceSource("navigation", later.Route.Revision));
        Assert.Null(journey.Update(later, Now).Notice);
    }

}
