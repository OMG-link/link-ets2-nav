using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Runtime;

/// <summary>Main-loop-owned waits. Adopt the whole completed window before executing its ready queries serially.</summary>
public sealed class MapRetryCoordinator:IMapRetryScheduler
{
    private sealed class Entry
    {
        public required (string,Type) Key;
        public required MissingMapObjects Missing;
        public required Func<MapRetryContext,object> Query;
        public required object Result;
        public required Func<object,MissingMapObjects?> Dependency;
        public long Revision=-1,LastAttempt=-1;
        public bool Running,Finished;
        public HashSet<long> Subscribers=[];
    }
    private readonly Dictionary<(string,Type),Entry> entries=[];
    // A cancellation token may fire off-loop. Only the owning loop changes the registry.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Entry Entry,long Id)> released=[];
    private MapRetryContext? current;
    private long subscriber;
    public void Reset(bool clearWindow=true)
    {DrainReleased();entries.Clear();if(clearWindow)current=null;}
    public void Adopt(NearbyMapSnapshot snapshot,long revision)
    {
        DrainReleased();
        foreach(var key in entries.Where(p=>p.Value.Subscribers.Count==0).Select(p=>p.Key).ToArray())entries.Remove(key);
        current=new(snapshot,revision);
        foreach(var entry in entries.Values.Where(e=>!e.Finished&&e.LastAttempt!=revision&&Ready(current,e.Missing)).ToArray())
        {
            entry.Running=true;entry.LastAttempt=revision;
            try
            {
                var result=entry.Query(current);
                if(entries.GetValueOrDefault(entry.Key)!=entry)continue;
                entry.Result=result;entry.Revision=revision;
                if(entry.Dependency(result) is {} missing)
                {AssertMissing(current,missing);entry.Missing=missing;}
                else entry.Finished=true;
            }
            finally{entry.Running=false;}
        }
    }
    // Each missing key is a wakeup for the whole operation. Waiting for every
    // distant SDK key at once would prevent useful local prefixes from updating.
    private static bool Ready(MapRetryContext context,MissingMapObjects missing)=>missing.Keys.Any(context.Contains);
    [System.Diagnostics.Conditional("DEBUG")]
    private static void AssertMissing(MapRetryContext context,MissingMapObjects missing)
    {
        if(missing.Keys.Any(context.Contains))throw new NavigationContractException("A retry dependency must be absent from the complete current query window.");
    }
    public MapQueryOperation<T> Query<T>(string key,Func<MapRetryContext,MapQueryResult<T>> query,CancellationToken cancellationToken=default)
    {
        DrainReleased();
#if DEBUG
        if(current is null)throw new NavigationContractException("A map query requires an adopted complete window.");
#endif
        cancellationToken.ThrowIfCancellationRequested();
        var identity=(key,typeof(T));
        if(entries.TryGetValue(identity,out var previous)&&previous.Revision==current!.Revision)
            return new((MapQueryResult<T>)previous.Result,Lease<T>(previous,cancellationToken));
        var result=query(current!);
        if(result.Issue is not MissingMapObjects missing)return new(result,null);
        AssertMissing(current!,missing);
        var entry=new Entry{Key=identity,Missing=missing,Query=c=>query(c),Result=result,
            Revision=current!.Revision,LastAttempt=current.Revision,Dependency=r=>((MapQueryResult<T>)r).Issue as MissingMapObjects};
        entries[identity]=entry;
        return new(result,Lease<T>(entry,cancellationToken));
    }
    private MapRetryHandle<T> Lease<T>(Entry entry,CancellationToken token)
    {
        long id=++subscriber;entry.Subscribers.Add(id);int disposed=0;
        var handle=new MapRetryHandle<T>(()=>
        {
            bool active=Volatile.Read(ref disposed)==0&&entries.GetValueOrDefault(entry.Key)==entry&&entry.Subscribers.Contains(id);
            var result=(MapQueryResult<T>)entry.Result;
            if(!active||entry.Revision!=current?.Revision)result=MapQueryResult<T>.Unavailable(new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,"Map retry lease is no longer current."));
            return new(active,entry.Running,entry.Revision,result);
        },()=>{Interlocked.Exchange(ref disposed,1);released.Enqueue((entry,id));});
        handle.CancelWith(token);return handle;
    }
    /// <summary>Attach a dependency discovered later in the same main-loop operation, retaining its known local result.</summary>
    public MapRetryHandle<T> WaitFor<T>(string key,MissingMapObjects missing,T localValue,Func<MapRetryContext,MapQueryResult<T>> query)
    {
        DrainReleased();
#if DEBUG
        if(current is null)throw new NavigationContractException("A wait requires an adopted complete window.");
#endif
        AssertMissing(current!,missing);
        var identity=(key,typeof(T));
        if(entries.TryGetValue(identity,out var entry)&&entry.Revision==current!.Revision)
        {
            entry.Missing=entry.Finished?missing:new MissingMapObjects(entry.Missing.Keys.Concat(missing.Keys),missing.Reason);
            entry.Finished=false;entry.Query=c=>query(c);entry.Result=new MapQueryResult<T>(localValue,entry.Missing);
        }
        else
        {
            entry=new(){Key=identity,Missing=missing,Query=c=>query(c),Result=new MapQueryResult<T>(localValue,missing),
                Revision=current!.Revision,LastAttempt=current.Revision,Dependency=r=>((MapQueryResult<T>)r).Issue as MissingMapObjects};
            entries[identity]=entry;
        }
        return Lease<T>(entry,default);
    }
    private void DrainReleased()
    {
        while(released.TryDequeue(out var release))
        {
            release.Entry.Subscribers.Remove(release.Id);
            // Retain this publication's local result across the old/new source
            // handoff, even when far-away route keys are still missing. With no
            // subscriber it is only a cache: Adopt removes it before dispatch.
            if(release.Entry.Subscribers.Count==0&&release.Entry.Revision!=current?.Revision&&entries.GetValueOrDefault(release.Entry.Key)==release.Entry)
                entries.Remove(release.Entry.Key);
        }
    }
}
