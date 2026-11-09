using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record MapScanRequest(string ResourceId, string SessionId, string GamePath,
    IReadOnlyList<string> LogLines, Vector3 Position)
{
    public TruckLib.ScsMap.Map? HostMap { get; init; }
    public bool PreparationOnly { get; init; }
    public RouteSnapshot? Route { get; init; }
    public double NavigationRemainingMeters { get; init; }
}

public interface IMapScanBackend : IDisposable
{
    NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken);
    NearbyMapSnapshot Read(MapScanRequest request, CancellationToken cancellationToken)
        => Read(request.Position, cancellationToken);
}

public sealed record MapScanState(NearbyMapSnapshot? Map, Vector3 Position, TimeSpan Elapsed,
    long Revision, string? Error, bool IsScanning)
{
    public bool IsPrepared { get; init; }
}

/// <summary>
/// A single worker owns all archive streams and mutable scanner caches. Only completed snapshots
/// cross the gate; neither reading nor projection holds the gate while doing actual work.
/// </summary>
public sealed class BackgroundMapScanner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<MapScanRequest, IMapScanBackend> factory;
    private readonly CancellationTokenSource stop;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task worker;
    private string? resourceId, sessionId, requestedRoute;
    private long generation;
    private (MapScanRequest Request, long Generation)? pending;
    private Vector3 requestedPosition = new(float.PositiveInfinity);
    private DateTimeOffset requestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private MapScanState state = new(null, default, default, 0, null, false);

    public BackgroundMapScanner(Func<MapScanRequest, IMapScanBackend>? factory = null,
        CancellationToken cancellationToken = default)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.factory = factory ?? (request => new OfficialBackend(request, stop.Token));
        worker = Task.Run(WorkAsync);
    }

    public MapScanState Capture() { lock (gate) return state; }

    /// <summary>Prepare resources without requiring or fabricating a vehicle position.</summary>
    public bool Prepare(MapScanRequest request, DateTimeOffset now)
    {
        lock (gate)
        {
            if (resourceId == request.ResourceId && (state.IsPrepared || state.Error is null || now < retryAfter)) return false;
            return Request(request with { PreparationOnly = true, SessionId = sessionId ?? "" }, now);
        }
    }

    /// <summary>Latest requested position replaces queued work; at most one scan runs at a time.</summary>
    public bool Request(MapScanRequest request, DateTimeOffset now)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return false;
            if (resourceId != request.ResourceId || sessionId != request.SessionId)
            {
                generation++;
                resourceId = request.ResourceId;
                sessionId = request.SessionId;
                pending = null;
                requestedAt = DateTimeOffset.MinValue;
                requestedPosition = new(float.PositiveInfinity);
                retryAfter = DateTimeOffset.MinValue;
                state = new(null, default, default, state.Revision, null, false);
            }
            if (now < retryAfter) return false;
            string? routeRevision = request.Route?.Revision;
            bool changedRoute = requestedRoute != routeRevision;
            if (changedRoute) generation++;
            if (!changedRoute && Vector3.Distance(request.Position, requestedPosition) < 25
                && now - requestedAt < TimeSpan.FromSeconds(3)) return false;
            requestedRoute = routeRevision;
            requestedPosition = request.Position;
            requestedAt = now;
            pending = (request, generation);
            state = state with { IsScanning = true };
            wake.Writer.TryWrite(true);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            generation++;
            resourceId = sessionId = requestedRoute = null;
            pending = null;
            requestedAt = retryAfter = DateTimeOffset.MinValue;
            requestedPosition = new(float.PositiveInfinity);
            state = new(null, default, default, state.Revision, null, false);
        }
    }

    private async Task WorkAsync()
    {
        IMapScanBackend? backend = null;
        string? backendResource = null;
        try
        {
            while (await wake.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                while (wake.Reader.TryRead(out _)) { }
                (MapScanRequest Request, long Generation)? job;
                lock (gate) { job = pending; pending = null; }
                if (job is not { } item) continue;
                var watch = Stopwatch.StartNew();
                NearbyMapSnapshot? completed = null;
                string? error = null;
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (backend is null || backendResource != item.Request.ResourceId)
                    {
                        backend?.Dispose();
                        backend = null;
                        backendResource = null;
                        backend = factory(item.Request);
                        backendResource = item.Request.ResourceId;
                    }
                    if (!item.Request.PreparationOnly)
                    {
                        bool needsNearby;
                        lock (gate) needsNearby = state.Map is null && item.Request.Route is not null;
                        if (needsNearby)
                        {
                            var nearby = backend.Read(item.Request with { Route = null }, stop.Token);
                            lock (gate)
                                if (item.Generation == generation && !stop.IsCancellationRequested)
                                    state = new(nearby, item.Request.Position, watch.Elapsed, state.Revision + 1, null, true) { IsPrepared = true };
                            watch.Restart();
                        }
                        completed = backend.Read(item.Request, stop.Token);
                    }
                    stop.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                lock (gate)
                {
                    // A load, teleport or mount change invalidates an in-flight result.
                    if (stop.IsCancellationRequested || item.Generation != generation) continue;
                    if (error is null)
                        state = new(completed, item.Request.Position, watch.Elapsed, state.Revision + 1, null, pending.HasValue) { IsPrepared = true };
                    else
                    {
                        // Keep an already usable snapshot when a refresh fails. Retry without
                        // sleeping the warning loop or spinning on a broken resource.
                        retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                        pending = null;
                        state = state with { Revision = state.Revision + 1, Error = error, IsScanning = false };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { backend?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        wake.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        stop.Dispose();
    }

    private sealed class OfficialBackend : IMapScanBackend
    {
        private readonly OfficialArchiveMount mount;
        private readonly NearbyCameraScanner scanner;
        public OfficialBackend(MapScanRequest request, CancellationToken token)
        {
            mount = OfficialArchiveMount.FromGameLog(request.GamePath, request.LogLines);
            try
            {
                scanner = new NearbyCameraScanner(mount.FileSystem, activeModPaths: mount.ActiveModPaths, hostMap: request.HostMap);
                scanner.Prepare(token);
            }
            catch { mount.Dispose(); throw; }
        }
        public NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken)
            => scanner.ReadNearby(position, 1100, cancellationToken);
        public NearbyMapSnapshot Read(MapScanRequest request, CancellationToken cancellationToken)
        {
            var coverage = MapScanCoverage.Create(request.Position, 1100, request.HostMap,
                request.Route, request.NavigationRemainingMeters);
            return scanner.ReadNearby(request.Position, 1100, cancellationToken, coverage);
        }
        public void Dispose() => mount.Dispose();
    }
}
