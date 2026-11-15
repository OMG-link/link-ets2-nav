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
    public IReadOnlyList<TruckLib.ScsMap.SectorCoordinate> RequiredSectors {get;init;}=[];
}

public interface IMapScanBackend : IDisposable
{
    RouteJourneyEstimator? JourneyEstimator=>null;
    NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken);
    NearbyMapSnapshot Read(MapScanRequest request, CancellationToken cancellationToken)
        => Read(request.Position, cancellationToken);
}

public sealed record MapScanState(NearbyMapSnapshot? Map, Vector3 Position, TimeSpan Elapsed,
    long Revision, string? Error, bool IsScanning)
{
    public bool IsPrepared { get; init; }
    public RouteJourneyEstimator? JourneyEstimator {get;init;}
}

/// <summary>
/// A single worker owns all archive streams and mutable scanner caches. Only completed snapshots
/// cross the gate; neither reading nor projection holds the gate while doing actual work.
/// </summary>
public sealed class BackgroundMapScanner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly object lifecycleGate=new();
    private readonly MapRetryCoordinator retries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> retryDiagnostics=new();
    public IMapRetryScheduler Retries=>retries;
    private MapScanRequest? latestRequest;
    private IReadOnlyList<TruckLib.ScsMap.SectorCoordinate> requiredSectors=[];
    private long retryGeneration;
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
        retries=new MapRetryCoordinator(SetDependencies,message=>retryDiagnostics.TryAdd(message,0));
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
            request=request with { PreparationOnly = true, SessionId = sessionId ?? "" };
        }
        return Request(request,now);
    }

    /// <summary>Latest requested position replaces queued work; at most one scan runs at a time.</summary>
    public bool Request(MapScanRequest request, DateTimeOffset now)
    {
        // Reset query lifetimes outside the scanner gate: cancellation belongs to callers.
        Action? cancel=null;
        try
        {
        lock (lifecycleGate)
        {
            bool reset;
            lock (gate) reset=resourceId!=request.ResourceId||sessionId!=request.SessionId
                ||(requestedRoute!=request.Route?.Revision
                   && !(latestRequest?.Route is {} previous&&request.Route is {} next&&RouteContinuity.IsForwardExtension(previous,next)));
            if(reset) cancel=retries.ResetScope();
            lock (gate)
            {
                if (stop.IsCancellationRequested) return false;
                if(reset) requiredSectors=[];
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
                if (reset) generation++;
                if (!changedRoute && Vector3.Distance(request.Position, requestedPosition) < 25
                    && now - requestedAt < TimeSpan.FromSeconds(3)) return false;
                requestedRoute = routeRevision;
                requestedPosition = request.Position;
                requestedAt = now;
                latestRequest=request;
                retryGeneration=retries.Generation;
                pending = (request with {RequiredSectors=requiredSectors}, generation);
                state = state with { IsScanning = true };
                wake.Writer.TryWrite(true);
                return true;
            }
        }
        }
        finally{cancel?.Invoke();}
    }

    public void Invalidate()
    {
        Action? cancel=null;
        try
        {
        lock(lifecycleGate)
        {
            cancel=retries.ResetScope();
            lock (gate)
            {
                generation++;
                resourceId = sessionId = requestedRoute = null;
                latestRequest=null; requiredSectors=[];
                pending = null;
                requestedAt = retryAfter = DateTimeOffset.MinValue;
                requestedPosition = new(float.PositiveInfinity);
                state = new(null, default, default, state.Revision, null, false);
            }
        }
        }
        finally{cancel?.Invoke();}
    }

    private void SetDependencies(IReadOnlyList<TruckLib.ScsMap.SectorCoordinate> sectors)
    {
        lock(gate)
        {
            if(stop.IsCancellationRequested||requiredSectors.SequenceEqual(sectors))return;
            requiredSectors=sectors.ToArray();
            if(latestRequest is not {PreparationOnly:false} request)return;
            pending=(request with {RequiredSectors=requiredSectors},generation);
            state=state with {IsScanning=true};
            wake.Writer.TryWrite(true);
        }
    }

    private NearbyMapSnapshot Decorate(NearbyMapSnapshot snapshot)=>snapshot with
    {Retries=retries,Diagnostics=snapshot.Diagnostics.Concat(retryDiagnostics.Keys).Distinct().ToArray()};

    private void Publish(NearbyMapSnapshot snapshot,long revision,long expectedGeneration)
    {
        if(!retries.Publish(snapshot,revision,expectedGeneration))return;
        foreach(var issue in snapshot.MapIssues)retries.Report(issue,expectedGeneration);
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
                long expectedRetryGeneration;
                lock (gate) { job = pending; pending = null;expectedRetryGeneration=retryGeneration; }
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
                    var readyBackend=backend??throw new InvalidOperationException("地图读取后端尚未初始化。");
                    if (!item.Request.PreparationOnly)
                    {
                        bool needsNearby;
                        lock (gate) needsNearby = state.Map is null && item.Request.Route is not null;
                        if (needsNearby)
                        {
                            var nearby = Decorate(readyBackend.Read(item.Request with { Route = null }, stop.Token));
                            long published=0;
                            lock (gate)
                                if (item.Generation == generation && !stop.IsCancellationRequested)
                                    { state = new(nearby, item.Request.Position, watch.Elapsed, state.Revision + 1, null, true) { IsPrepared = true, JourneyEstimator = readyBackend.JourneyEstimator }; published=state.Revision; }
                            if(published!=0)Publish(nearby,published,expectedRetryGeneration);
                            watch.Restart();
                        }
                        completed = Decorate(readyBackend.Read(item.Request, stop.Token));
                    }
                    stop.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                long completedRevision=0;
                lock (gate)
                {
                    // A load, teleport or mount change invalidates an in-flight result.
                    if (stop.IsCancellationRequested || item.Generation != generation) continue;
                    if (error is null)
                    {
                        state = new(completed, item.Request.Position, watch.Elapsed, state.Revision + 1, null, pending.HasValue) { IsPrepared = true, JourneyEstimator = backend?.JourneyEstimator };
                        if(completed is not null)completedRevision=state.Revision;
                    }
                    else
                    {
                        // Keep an already usable snapshot when a refresh fails. Retry without
                        // sleeping the warning loop or spinning on a broken resource.
                        retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                        pending = null;
                        state = state with { Revision = state.Revision + 1, Error = error, IsScanning = false };
                    }
                }
                if(completed is not null&&completedRevision!=0)Publish(completed,completedRevision,expectedRetryGeneration);
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
        await retries.DisposeAsync().ConfigureAwait(false);
        stop.Dispose();
    }

    private sealed class OfficialBackend : IMapScanBackend
    {
        private readonly GameDataManager data;
        private readonly NearbyCameraScanner scanner;
        public OfficialBackend(MapScanRequest request, CancellationToken token)
        {
            data=GameDataManager.Open(request.GamePath,request.LogLines,request.HostMap);
            try
            {
                scanner=new NearbyCameraScanner(data);
                scanner.Prepare(token);
            }
            catch { data.Dispose(); throw; }
        }
        public RouteJourneyEstimator? JourneyEstimator=>scanner.JourneyEstimator;
        public NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken)
            => scanner.ReadNearby(position, 1100, cancellationToken);
        public NearbyMapSnapshot Read(MapScanRequest request, CancellationToken cancellationToken)
        {
            var coverage = MapScanCoverage.CreateFromData(request.Position, 1100, data.Map,
                request.Route, request.NavigationRemainingMeters).WithAdditionalSectors(request.RequiredSectors);
            return scanner.ReadNearby(request.Position, 1100, cancellationToken, coverage);
        }
        public void Dispose() => data.Dispose();
    }
}

