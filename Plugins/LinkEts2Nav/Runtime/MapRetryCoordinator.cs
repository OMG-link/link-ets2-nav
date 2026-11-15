using System.Threading.Channels;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Runtime;

/// <summary>Queries consume published snapshots and return values; they must not publish external state.
/// Loader demand notifications are asynchronous, coalesced, and serialized independently of query callbacks.</summary>
public sealed class MapRetryCoordinator:IMapRetryScheduler,IAsyncDisposable
{
    private sealed class Entry
    {
        public required (string Key,Type Type) Key;public required MissingMapSectors Missing;
        public required Func<MapRetryContext,CancellationToken,ValueTask<(object Value,MapQueryIssue? Issue)>> Query;
        public required object Result;public long ResultRevision=-1,LastAttempt=-1,Serial;public MapQueryIssue? Error;
        public bool Running,Finished;public HashSet<long> Subscribers=[];public CancellationTokenSource? Cancellation;
    }
    private readonly object gate=new();
    private readonly Dictionary<(string,Type),Entry> entries=[];
    private readonly Dictionary<string,MissingMapSectors> reported=[];
    private readonly Action<IReadOnlyList<SectorCoordinate>> request;
    private readonly Action<string>? log;
    private readonly Channel<bool> wake=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropWrite});
    // Notifications carry no captured sector list. A single consumer reads the
    // latest scope immediately before calling the loader, so a delayed old
    // notification cannot overwrite a newer Reset/Release demand.
    private readonly Channel<bool> demandWake=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){SingleReader=true,FullMode=BoundedChannelFullMode.DropWrite});
    private readonly CancellationTokenSource stop=new();private readonly Task worker;
    private readonly Task demandWorker;
    private MapRetryContext? current;private long subscriber,epoch,scopeRevision;private bool disposed;
    public long ScopeRevision {get{lock(gate)return scopeRevision;}}
    public long Generation {get{lock(gate)return epoch;}}
    public MapRetryCoordinator(Action<IReadOnlyList<SectorCoordinate>> request,Action<string>? log=null)
    {this.request=request;this.log=log;worker=Task.Run(WorkAsync);demandWorker=Task.Run(PublishDemandsAsync);}
    private bool Local(MissingMapSectors missing)=>current?.Snapshot.Coverage is {} coverage
        &&missing.QueryAnchor is {} anchor&&coverage.ContainsQueryAnchor(anchor);
    private bool Ready(MissingMapSectors missing)=>current is {} context&&missing.Sectors.All(context.Snapshot.ReadySectors.Contains);
    private IReadOnlyList<SectorCoordinate> Demands()=>reported.Values.Concat(entries.Values.Select(e=>e.Missing))
        .Where(Local).SelectMany(m=>m.Sectors).Distinct().OrderBy(s=>s.X).ThenBy(s=>s.Z).ToArray();
    public void Report(MapQueryIssue issue)
        =>Report(issue,Generation);
    public void Report(MapQueryIssue issue,long expectedGeneration)
    {
        if(issue is not MissingMapSectors missing)return;
        lock(gate)
        {
            if(disposed||epoch!=expectedGeneration||!Local(missing))return;
            string identity=missing.Reason+"@"+missing.QueryAnchor+":"+string.Join(';',missing.Sectors.Select(s=>$"{s.X},{s.Z}"));
            if(reported.ContainsKey(identity)||Ready(missing))return;
            reported[identity]=missing;
        }
        DemandChanged();
    }
    public MapRetryHandle<T> Register<T>(string key,MissingMapSectors missing,
        Func<MapRetryContext,CancellationToken,ValueTask<MapQueryResult<T>>> retry,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);ArgumentNullException.ThrowIfNull(retry);
        Entry entry;long id;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);var identity=(key,typeof(T));
            if(entries.TryGetValue(identity,out var completed)&&completed.Finished&&completed.ResultRevision!=current?.Revision)
                entries.Remove(identity); // Completed results are one-shot and version-scoped; a new registration gets a new query.
            if(!entries.TryGetValue(identity,out entry!))
            {
                entry=new(){Key=identity,Missing=missing,Result=MapQueryResult<T>.Unavailable(missing),Query=async(c,t)=>
                    {var value=await retry(c,t).ConfigureAwait(false);return(value,value.Issue);}};
                entries.Add(identity,entry);
            }
            else if(!entry.Finished)
                entry.Missing=new MissingMapSectors(entry.Missing.Sectors.Concat(missing.Sectors),missing.Reason){QueryAnchor=missing.QueryAnchor??entry.Missing.QueryAnchor};
            id=++subscriber;entry.Subscribers.Add(id);
        }
        var handle=new MapRetryHandle<T>(()=>Capture<T>(entry,id),()=>Release(entry,id));
        handle.CancelWith(cancellationToken);DemandChanged();wake.Writer.TryWrite(true);return handle;
    }
    private MapRetryStatus<T> Capture<T>(Entry entry,long id)
    {
        lock(gate)
        {
            bool active=!disposed&&entries.GetValueOrDefault(entry.Key)==entry&&entry.Subscribers.Contains(id);
            var result=(MapQueryResult<T>)entry.Result;
            if(entry.Error is {} error)result=MapQueryResult<T>.Unavailable(error);
            if(!active||result.IsSuccess&&entry.ResultRevision!=current?.Revision)
                result=MapQueryResult<T>.Unavailable(new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,"Retry lease or map generation is no longer current."));
            return new(active,entry.Running,entry.ResultRevision,result);
        }
    }
    private void Release(Entry entry,long id)
    {
        CancellationTokenSource? cancel=null;
        lock(gate)
        {
            entry.Subscribers.Remove(id);
            if(entry.Subscribers.Count==0&&entries.GetValueOrDefault(entry.Key)==entry)
            {entries.Remove(entry.Key);entry.Serial++;cancel=entry.Cancellation;}
        }
        SafeCancel(cancel);DemandChanged();
    }
    public void Reset()
    {
        CancellationTokenSource[] cancel;
        lock(gate)
        {
            epoch++;scopeRevision++;current=null;cancel=entries.Values.Select(e=>e.Cancellation).OfType<CancellationTokenSource>().ToArray();
            entries.Clear();reported.Clear();
        }
        foreach(var c in cancel)SafeCancel(c);DemandChanged();
    }
    /// <summary>Only call after a successful full derived snapshot became visible to consumers.</summary>
    public void Publish(NearbyMapSnapshot snapshot,long revision)
        =>Publish(snapshot,revision,Generation);
    /// <summary>Reject an in-flight load if its resource/session generation was reset before publication.</summary>
    public bool Publish(NearbyMapSnapshot snapshot,long revision,long expectedGeneration)
    {
        CancellationTokenSource[] cancel;
        lock(gate)
        {
            if(disposed||epoch!=expectedGeneration)return false;scopeRevision++;current=new(snapshot,revision);
            cancel=entries.Values.Where(e=>e.Running).Select(e=>e.Cancellation).OfType<CancellationTokenSource>().ToArray();
            foreach(var entry in entries.Values.Where(e=>e.Running)){entry.Serial++;entry.Running=false;}
        }
        foreach(var c in cancel)SafeCancel(c);DemandChanged();wake.Writer.TryWrite(true);
        return true;
    }
    private void DemandChanged()=>demandWake.Writer.TryWrite(true);
    private async Task PublishDemandsAsync()
    {
        // Separate from query callbacks: a slow retry cannot delay cancellation
        // or prevent the loader from receiving newly required sectors.
        while(await demandWake.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while(demandWake.Reader.TryRead(out _)){}
            IReadOnlyList<SectorCoordinate> demands;
            lock(gate)demands=disposed?[]:Demands();
            try{request(demands);}
            catch(Exception ex)
            {
                // One failing consumer must not permanently kill the notifier.
                try{log?.Invoke("Map demand notification failed: "+ex.GetType().Name+": "+ex.Message);}catch{}
            }
        }
    }
    private async Task WorkAsync()
    {
        try
        {
            while(await wake.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                while(wake.Reader.TryRead(out _)){}
                while(true)
                {
                    Entry? job;MapRetryContext context;long serial,scope;CancellationTokenSource cancellation;
                    lock(gate)
                    {
                        if(current is null)break;
                        job=entries.Values.FirstOrDefault(e=>!e.Finished&&!e.Running&&e.LastAttempt!=current.Revision&&Local(e.Missing)&&Ready(e.Missing));
                        if(job is null)break;
                        context=current;scope=epoch;serial=++job.Serial;job.Running=true;job.LastAttempt=context.Revision;
                        cancellation=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);job.Cancellation=cancellation;
                    }
                    object? result=null;MapQueryIssue? issue=null;Exception? error=null;
                    try{(result,issue)=await job.Query(context,cancellation.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
                    catch(Exception ex){error=ex;}
                    bool demandsChanged=false;
                    lock(gate)
                    {
                        if(!disposed&&scope==epoch&&job.Serial==serial&&current?.Revision==context.Revision&&entries.GetValueOrDefault(job.Key)==job)
                        {
                            job.Running=false;
                            job.Error=error is null?null:new MapQueryFailure(MapQueryFailureKind.InvalidData,error.GetType().Name+": "+error.Message);
                            if(result is not null){job.Result=result;job.ResultRevision=context.Revision;}
                            if(issue is MissingMapSectors missing&&!Ready(missing)){job.Missing=missing;demandsChanged=true;}
                            else
                            {
                                job.Finished=true;
                                if(issue is MissingMapSectors)job.Error=new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,
                                    "Query still requires sectors already in this published snapshot; loading alone cannot resolve it.");
                            }
                        }
                        if(ReferenceEquals(job.Cancellation,cancellation))job.Cancellation=null;
                    }
                    cancellation.Dispose();
                    if(error is not null)log?.Invoke("Map retry failed: "+error.GetType().Name+": "+error.Message);
                    if(demandsChanged)DemandChanged();
                }
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
    }
    private void SafeCancel(CancellationTokenSource? cancellation)
    {
        if(cancellation is null)return;
        try{cancellation.Cancel();}
        catch(ObjectDisposedException){}
        catch(AggregateException ex){log?.Invoke("Map retry cancellation callback failed: "+ex.Message);}
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed)return;disposed=true;}
        Reset();stop.Cancel();wake.Writer.TryComplete();demandWake.Writer.TryComplete();
        try{await worker.ConfigureAwait(false);}
        finally{await demandWorker.ConfigureAwait(false);stop.Dispose();}
    }
}
