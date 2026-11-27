using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record MapScanRequest(string ResourceId, string SessionId, string GamePath,
    IReadOnlyList<string> LogLines, Vector3 Position)
{
    public TruckLib.ScsMap.Map? HostMap { get; init; }
    internal bool UseNavigationIndex { get; init; }
    public bool PreparationOnly { get; init; }
    public RouteSnapshot? Route { get; init; }
    public long RouteGeneration {get;init;}
    public double NavigationRemainingMeters { get; init; }
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
    internal LoadedNavigationRoute? LoadedRoute {get;init;}
    internal RouteJourneyEstimate? Estimate(RouteSnapshot route)
        =>LoadedRoute is {} loaded?loaded.Estimate(route):JourneyEstimator?.Estimate(route);
}

/// <summary>
/// One worker owns mutable nearby scan state. Archive reads and route preparation run outside the gate.
/// </summary>
public sealed class BackgroundMapScanner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly MapRetryCoordinator retries;
    public IMapRetryScheduler Retries=>retries;
    private long? routeScope;
    private MapScanRequest? inFlight;
    private readonly Queue<MapScanState> completedWindows=[];
    private long producedRevision;
    private readonly Func<MapScanRequest, IMapScanBackend> factory;
    private readonly CancellationTokenSource stop;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task worker;
    private string? resourceId, sessionId, requestedRoute;
    private long generation;
    private (MapScanRequest Request,long Generation,bool RouteRefresh)? pending;
    private MapScanRequest? lastScan;
    private Vector3 requestedPosition = new(float.PositiveInfinity);
    private DateTimeOffset requestedAt = DateTimeOffset.MinValue;
    private DateTimeOffset retryAfter = DateTimeOffset.MinValue;
    private MapScanState state = new(null, default, default, 0, null, false);

    public BackgroundMapScanner(Func<MapScanRequest, IMapScanBackend>? factory = null,
        CancellationToken cancellationToken = default)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        retries=new MapRetryCoordinator();
        this.factory = factory ?? (request => new OfficialBackend(request,stop.Token,()=>RefreshRoute(request.ResourceId)));
        worker = Task.Run(WorkAsync);
    }

    /// <summary>The plugin driver observes failed worker tasks at its outer FATAL boundary.</summary>
    public void ThrowIfFaulted()
    {
        if(worker.IsCompleted)worker.GetAwaiter().GetResult();
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

    /// <summary>Only position updates enqueue scans; pending work coalesces to the latest placement.</summary>
    public bool Request(MapScanRequest request,DateTimeOffset now)
    {
        lock(gate)
        {
            bool newSession=resourceId!=request.ResourceId||sessionId!=request.SessionId;
            bool changedRoute=requestedRoute!=request.Route?.Revision;
            if(newSession){retries.Reset();routeScope=request.RouteGeneration;}
            else ObserveRoute(request.RouteGeneration);
            if(newSession)
            {
                generation++;resourceId=request.ResourceId;sessionId=request.SessionId;
                pending=null;lastScan=null;completedWindows.Clear();requestedPosition=new(float.PositiveInfinity);
                requestedAt=retryAfter=DateTimeOffset.MinValue;
                state=new(null,default,default,state.Revision,null,false);
            }
            requestedRoute=request.Route?.Revision;
            if(!request.PreparationOnly)lastScan=request;
            if(stop.IsCancellationRequested||now<retryAfter)return false;
            if(inFlight is {} active&&active.ResourceId==request.ResourceId&&active.SessionId==request.SessionId
                &&active.Position==request.Position&&active.Route?.Revision==request.Route?.Revision)
            {
                pending=pending is {RouteRefresh:true}?(request,generation,true):null;
                requestedPosition=request.Position;requestedAt=now;return false;
            }
            if(!changedRoute&&!request.PreparationOnly&&request.Position==requestedPosition)return false;
            if(!changedRoute&&!newSession&&!request.PreparationOnly&&Vector3.Distance(request.Position,requestedPosition)<25
                &&now-requestedAt<TimeSpan.FromSeconds(3))return false;
            requestedPosition=request.Position;requestedAt=now;
            pending=(request,generation,pending is {RouteRefresh:true});
            state=state with{IsScanning=true};wake.Writer.TryWrite(true);return true;
        }
    }
    /// <summary>Cancel obsolete route operations without requesting a scan. Called on the main loop even without a new position sample.</summary>
    public void ObserveRoute(long routeGeneration)
    {
        if(routeScope is {} previous&&previous!=routeGeneration)retries.Reset(false);
        routeScope=routeGeneration;
    }
    public void Invalidate()
    {
        lock(gate)
        {
            retries.Reset();generation++;resourceId=sessionId=requestedRoute=null;routeScope=null;
            pending=null;lastScan=null;completedWindows.Clear();requestedPosition=new(float.PositiveInfinity);
            requestedAt=retryAfter=DateTimeOffset.MinValue;
            state=new(null,default,default,state.Revision,null,false);
        }
    }

    // A ready route also refreshes a stationary player's map and journey estimate.
    internal void RefreshRoute(string sourceResource)
    {
        lock(gate)
        {
            if(stop.IsCancellationRequested||resourceId!=sourceResource||lastScan is not {} request)return;
            pending=(request,generation,true);
            state=state with{IsScanning=true};
            wake.Writer.TryWrite(true);
        }
    }
    /// <summary>Main-loop boundary: adopt each complete window, then execute its ready callbacks before any judgement.</summary>
    public MapScanState AdoptReady()
    {
        int count;lock(gate)count=completedWindows.Count;
        for(int i=0;i<count;i++)
        {
            MapScanState adopted;long mapRevision;
            lock(gate)
            {
                if(!completedWindows.TryDequeue(out var next))return state;
                mapRevision=next.Revision;
                // A nearby window may have completed before the route scan's
                // recoverable I/O error. Adopting it must retain that newer error.
                adopted=next with{IsScanning=state.IsScanning,
                    Error=state.Revision>next.Revision?state.Error:next.Error,
                    Revision=Math.Max(state.Revision,next.Revision)};state=adopted;
            }
            if(adopted.Map is {} map)retries.Adopt(map,mapRevision);
        }
        return Capture();
    }
    private NearbyMapSnapshot Decorate(NearbyMapSnapshot snapshot)=>snapshot with{Retries=retries};
    private void EnqueueWindow(NearbyMapSnapshot? snapshot,MapScanRequest request,TimeSpan elapsed,bool scanning,
        IMapScanBackend? backend,long expectedGeneration)
    {
        lock(gate)
        {
            if(expectedGeneration!=generation||stop.IsCancellationRequested)return;
            completedWindows.Enqueue(new(snapshot,request.Position,elapsed,++producedRevision,null,scanning)
                {IsPrepared=true,JourneyEstimator=backend?.JourneyEstimator,LoadedRoute=(backend as OfficialBackend)?.LoadedRoute});
            state=state with{IsScanning=scanning};
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
                (MapScanRequest Request,long Generation,bool RouteRefresh)? job;
                lock (gate) { job = pending; pending = null;inFlight=job?.Request; }
                if (job is not { } item) continue;
                var watch = Stopwatch.StartNew();
                NearbyMapSnapshot? completed = null;
                string? error = null;
                stop.Token.ThrowIfCancellationRequested();
                if (backend is null || backendResource != item.Request.ResourceId)
                {
                    var previous=backend;
                    backend=null; backendResource=null;
                    previous?.Dispose();
                    backend=ReadResource(()=>factory(item.Request),out error);
                    if(error is null)backendResource=item.Request.ResourceId;
                }
                if(error is null&&!item.Request.PreparationOnly)
                {
                    var readyBackend=backend??throw new InvalidOperationException("Map scan backend was not initialized.");
                    bool needsNearby;
                    lock (gate) needsNearby = state.Map is null && !completedWindows.Any(p=>p.Map is not null) && item.Request.Route is not null;
                    if (needsNearby)
                    {
                        var read=ReadResource(()=>readyBackend.Read(item.Request with { Route = null },stop.Token),out error);
                        if(error is null)
                        {
                            var nearby=Decorate(read!);
                            EnqueueWindow(nearby,item.Request,watch.Elapsed,true,readyBackend,item.Generation);
                            watch.Restart();
                        }
                    }
                    if(error is null)
                    {
                        var read=ReadResource(()=>readyBackend.Read(item.Request,stop.Token),out error);
                        if(error is null)completed=Decorate(read!);
                    }
                }
                stop.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    inFlight=null;
                    if(stop.IsCancellationRequested||item.Generation!=generation)continue;
                    if(error is null)
                        EnqueueWindow(completed,item.Request,watch.Elapsed,pending.HasValue,backend,item.Generation);
                    else
                    {
                        // Keep an already usable snapshot when a refresh fails. Retry without
                        // sleeping the warning loop or spinning on a broken resource.
                        retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                        pending = null;
                        state = state with { Revision = ++producedRevision, Error = error, IsScanning = false };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { backend?.Dispose(); }
    }

    // Only resource access failures are recoverable here. Invalid serialized
    // input, callbacks, publication and cleanup errors belong to the driver.
    private static T? ReadResource<T>(Func<T> read,out string? error) where T:class
    {
        try
        {
            var value=read();error=null;return value;
        }
        catch(IOException ex)
        {error=ex.GetType().Name+": "+ex.Message;return null;}
        catch(UnauthorizedAccessException ex)
        {error=ex.GetType().Name+": "+ex.Message;return null;}
    }

    public async ValueTask DisposeAsync()
    {
        try { stop.Cancel(); }
        finally
        {
            wake.Writer.TryComplete();
            try { await worker.ConfigureAwait(false); }
            finally
            {
                stop.Dispose();
            }
        }
    }

    private sealed class OfficialBackend : IMapScanBackend
    {
        private readonly GameDataManager data;
        private readonly NearbyCameraScanner scanner;
        private readonly NavigationRouteCache? routes;
        internal LoadedNavigationRoute? LoadedRoute {get;private set;}
        public OfficialBackend(MapScanRequest request,CancellationToken token,Action refresh)
        {
            data=request.UseNavigationIndex
                ?GameDataManager.OpenNavigationOnly(request.GamePath,request.LogLines,token)
                :GameDataManager.Open(request.GamePath,request.LogLines,request.HostMap);
            bool prepared=false;
            try
            {
                scanner=new NearbyCameraScanner(data);
                scanner.Prepare(token);
                if(data.NavigationIndexFile is {} indexFile)routes=new(data,indexFile,refresh);
                prepared=true;
            }
            finally { if(!prepared)data.Dispose(); }
        }
        public RouteJourneyEstimator? JourneyEstimator=>scanner.JourneyEstimator;
        public NearbyMapSnapshot Read(Vector3 position, CancellationToken cancellationToken)
            => scanner.ReadNearby(position, 1100, cancellationToken);
        public NearbyMapSnapshot Read(MapScanRequest request, CancellationToken cancellationToken)
        {
            LoadedRoute=routes?.Resolve(request.Route);
            var coverage=routes is null
                ?MapScanCoverage.CreateFromData(request.Position,1100,data.Map,request.Route,request.NavigationRemainingMeters)
                :MapScanCoverage.Create(request.Position,1100,LoadedRoute?.Map,request.Route,request.NavigationRemainingMeters);
            return scanner.ReadNearby(request.Position, 1100, cancellationToken, coverage);
        }
        public void Dispose()
        {
            try { routes?.Dispose(); }
            finally { data.Dispose(); }
        }
    }
}

