using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class BackgroundMapScannerTests
{
    private static MapScanRequest Request(string session = "session", string resource = "resource", float x = 0)
        => new(resource, session, "game", [], new(x, 0, 0));
    private static NearbyMapSnapshot Map(string id)
        => new(new TruckLib.ScsMap.Map(), [new CameraObservation(id, default, default, Vector3.UnitX, false, "", "")], [], []);
    private sealed class Backend(Func<Vector3, CancellationToken, NearbyMapSnapshot> read, Action? dispose = null,RouteJourneyEstimator? estimator=null) : IMapScanBackend
    {
        public NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken) => read(position, cancellationToken);
        public void Dispose() => dispose?.Invoke();
        public RouteJourneyEstimator? JourneyEstimator=>estimator;
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class RouteBackend(Func<MapScanRequest, CancellationToken, NearbyMapSnapshot> read) : IMapScanBackend
    {
        public NearbyMapSnapshot Read(Vector3 p, CancellationToken token) => throw new InvalidOperationException();
        public NearbyMapSnapshot Read(MapScanRequest request, CancellationToken token) => read(request, token);
        public void Dispose() { }
    }
    [Fact]
    public async Task PublishesNearbyBeforeSlowRouteExpansionAndRerouteSupersedesOldWork()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var near = Map("near"); var updated = Map("new-route");
        await using var scanner = new BackgroundMapScanner(_ => new RouteBackend((request, token) =>
        {
            if (request.Route is null) return near;
            if (request.Route.Revision == "old") { started.TrySetResult(); release.Wait(token); return Map("old"); }
            return updated;
        }), TestContext.Current.CancellationToken);
        var now=DateTimeOffset.UtcNow;
        var route=new RouteSnapshot([],"old",RouteAvailability.Available,now);
        scanner.Request(Request() with { Route=route, NavigationRemainingMeters=10000 },now);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8),TestContext.Current.CancellationToken);
        try
        {
            Assert.Same(near,scanner.Capture().Map);
            Assert.True(scanner.Capture().IsScanning);
            // Same position/time must not suppress a changed route.
            Assert.True(scanner.Request(Request() with{Route=route with{Revision="new"}},now));
        }
        finally { release.Set(); }
        await WaitFor(()=>ReferenceEquals(updated,scanner.Capture().Map));
        Assert.Equal(2,scanner.Capture().Revision); // nearby + new route; old result was discarded
    }

    [Fact]
    public async Task PreparedJourneyMetadataIsPublishedBeforeAnyNearbyMapRead()
    {
        var estimator=new RouteJourneyEstimator(new TruckLib.ScsMap.Map(),(_,_)=>new(80,"test"));
        await using var scanner=new BackgroundMapScanner(_=>new Backend((_,_)=>throw new InvalidOperationException("No map scan should run."),estimator:estimator));
        Assert.True(scanner.Prepare(Request(),DateTimeOffset.UtcNow));
        await WaitFor(()=>scanner.Capture().IsPrepared);
        Assert.Null(scanner.Capture().Map);Assert.Same(estimator,scanner.Capture().JourneyEstimator);
    }
    [Fact]
    public async Task PreparationWithoutPositionCreatesBackendButDoesNotScanAndIsReused()
    {
        int created = 0, reads = 0;
        await using var scanner = new BackgroundMapScanner(_ =>
        {
            Interlocked.Increment(ref created);
            return new Backend((_, _) => { Interlocked.Increment(ref reads); return Map("ready"); });
        }, TestContext.Current.CancellationToken);
        Assert.True(scanner.Prepare(Request(), DateTimeOffset.UtcNow));
        await WaitFor(() => scanner.Capture().Revision == 1);
        Assert.Equal(1, created);
        Assert.Equal(0, reads);
        Assert.Null(scanner.Capture().Map);
        Assert.False(scanner.Prepare(Request(), DateTimeOffset.UtcNow));
        scanner.Request(Request(x: 100), DateTimeOffset.UtcNow);
        await WaitFor(() => scanner.Capture().Map is not null);
        Assert.Equal(1, created);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SlowRefreshKeepsReadyMapReadableAndWarningsRunning()
    {
        var first = Map("first");
        var second = Map("second");
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var scanner = new BackgroundMapScanner(_ => new Backend((_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return first;
            started.TrySetResult();
            release.Wait(token);
            return second;
        }), TestContext.Current.CancellationToken);
        scanner.Request(Request(), DateTimeOffset.UtcNow);
        await WaitFor(() => scanner.Capture().Map is not null);
        scanner.Request(Request(x: 30), DateTimeOffset.UtcNow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        try
        {
            // Capture must not wait for the refresh's I/O. Use a bounded task so a
            // regression holding the publication lock during Read fails without hanging.
            var snapshot = await Task.Run(scanner.Capture, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Same(first, snapshot.Map);
            Assert.True(snapshot.IsScanning);
            var engine = new CameraWarningEngine();
            var now = DateTimeOffset.UtcNow;
            Assert.Single(engine.Update(new("session", "", false, true, now,
                [new("first", CameraApproach.UnavoidableAhead, 499, 499)]), now));
            Assert.Same(first, scanner.Capture().Map);
        }
        finally { release.Set(); }
        await WaitFor(() => ReferenceEquals(scanner.Capture().Map, second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessionOrResourceCannotPublishAndArchivesAreNotDisposedDuringRead(bool resourceChange)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Map("current");
        int calls = 0, disposed = 0;
        await using var scanner = new BackgroundMapScanner(request => new Backend((_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult(); release.Wait(token); return Map("stale");
            }
            return current;
        }, () => Interlocked.Increment(ref disposed)), TestContext.Current.CancellationToken);
        scanner.Request(Request(), DateTimeOffset.UtcNow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        try
        {
            scanner.Request(resourceChange ? Request(resource: "new") : Request(session: "new"), DateTimeOffset.UtcNow);
            Assert.Null(scanner.Capture().Map);
            Assert.Equal(0, disposed);
            Assert.Equal(1, calls); // no parallel reads, including across resource changes
        }
        finally { release.Set(); }
        await WaitFor(() => scanner.Capture().Map is not null);
        Assert.Same(current, scanner.Capture().Map);
        Assert.Equal(resourceChange ? 1 : 0, disposed);
        Assert.Equal(1, scanner.Capture().Revision); // stale completion never published
    }

    [Fact]
    public async Task BusyWorkerCoalescesPositionsRatherThanBuildingAQueue()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<float>();
        await using var scanner = new BackgroundMapScanner(_ => new Backend((position, token) =>
        {
            seen.Enqueue(position.X);
            if (seen.Count == 1) { started.TrySetResult(); release.Wait(token); }
            return Map(position.X.ToString());
        }), TestContext.Current.CancellationToken);
        scanner.Request(Request(), DateTimeOffset.UtcNow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        try
        {
            scanner.Request(Request(x: 30), DateTimeOffset.UtcNow);
            scanner.Request(Request(x: 60), DateTimeOffset.UtcNow);
            scanner.Request(Request(x: 90), DateTimeOffset.UtcNow);
        }
        finally { release.Set(); }
        await WaitFor(() => scanner.Capture().Position.X == 90);
        Assert.Equal(new[] { 0f, 90f }, seen.ToArray());
    }

    [Fact]
    public async Task RefreshFailureRetainsSnapshotAndRetriesWithoutWaitingInCaller()
    {
        var ready = Map("ready");
        int calls = 0;
        await using var scanner = new BackgroundMapScanner(_ => new Backend((_, _) =>
            Interlocked.Increment(ref calls) == 2 ? throw new IOException("failed") : ready), TestContext.Current.CancellationToken);
        scanner.Request(Request(), DateTimeOffset.UtcNow);
        await WaitFor(() => scanner.Capture().Map is not null);
        scanner.Request(Request(x: 30), DateTimeOffset.UtcNow);
        await WaitFor(() => scanner.Capture().Error is not null);
        Assert.Same(ready, scanner.Capture().Map);
        Assert.False(scanner.Request(Request(x: 60), DateTimeOffset.UtcNow));
        Assert.True(scanner.Request(Request(x: 60), DateTimeOffset.UtcNow.AddSeconds(6)));
        await WaitFor(() => scanner.Capture().Error is null);
        Assert.Same(ready, scanner.Capture().Map);
    }

    [Fact]
    public async Task InvalidateDiscardsInFlightMapAndShutdownWaitsBeforeDisposingArchive()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        var scanner = new BackgroundMapScanner(_ => new Backend((_, _) =>
        {
            started.TrySetResult();
            // Intentionally ignore scanner cancellation, as an archive read may do.
            release.Wait(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            return Map("stale");
        }, () => disposed = true), TestContext.Current.CancellationToken);
        scanner.Request(Request(), DateTimeOffset.UtcNow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        scanner.Invalidate();
        var stopping = scanner.DisposeAsync().AsTask();
        try { Assert.False(stopping.IsCompleted); Assert.False(disposed); Assert.Null(scanner.Capture().Map); }
        finally { release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken); }
        Assert.True(disposed);
        Assert.Null(scanner.Capture().Map);
    }
}
