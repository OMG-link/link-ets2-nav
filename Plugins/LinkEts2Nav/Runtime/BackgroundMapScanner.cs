using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record MapScanRequest(string ResourceId, string SessionId, string GamePath,
    IReadOnlyList<string> LogLines, Vector3 Position)
{
    public TruckLib.ScsMap.Map? HostMap { get; init; }
}

public interface IMapScanBackend : IDisposable
{
    NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken);
}

public sealed record MapScanState(NearbyMapSnapshot? Map, Vector3 Position, TimeSpan Elapsed,
    long Revision, string? Error, bool IsScanning);

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
    private string? resourceId, sessionId;
    private long generation;
    private (MapScanRequest Request, long Generation)? pending;
    private Vector3 requestedPosition = new(float.PositiveInfinity);
    private DateTimeOffset requestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private MapScanState state = new(null, default, default, 0, null, false);

    public BackgroundMapScanner(Func<MapScanRequest, IMapScanBackend>? factory = null,
        CancellationToken cancellationToken = default)
    {
        this.factory = factory ?? (request => new OfficialBackend(request));
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(WorkAsync);
    }

    public MapScanState Capture() { lock (gate) return state; }

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
            if (Vector3.Distance(request.Position, requestedPosition) < 25
                && now - requestedAt < TimeSpan.FromSeconds(3)) return false;
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
            resourceId = sessionId = null;
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
                    completed = backend.Read(item.Request.Position, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                lock (gate)
                {
                    // A load, teleport or mount change invalidates an in-flight result.
                    if (stop.IsCancellationRequested || item.Generation != generation) continue;
                    if (error is null)
                        state = new(completed, item.Request.Position, watch.Elapsed, state.Revision + 1, null, pending.HasValue);
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
        public OfficialBackend(MapScanRequest request)
        {
            mount = OfficialArchiveMount.FromGameLog(request.GamePath, request.LogLines);
            scanner = new NearbyCameraScanner(mount.FileSystem, activeModPaths: mount.ActiveModPaths, hostMap: request.HostMap);
        }
        public NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken)
            => scanner.ReadNearby(position, 1100, cancellationToken);
        public void Dispose() => mount.Dispose();
    }
}
