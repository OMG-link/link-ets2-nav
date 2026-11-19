using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Runtime;

public sealed record MapRetryContext(NearbyMapSnapshot Snapshot,long Revision)
{
    public bool Contains(MapObjectKey key)=>key.Kind switch
    {
        MapObjectKind.Node=>Snapshot.Map.Nodes.TryGetValue(key.Uid,out var node)&&node is Node,
        MapObjectKind.Item=>Snapshot.Map.MapItems.ContainsKey(key.Uid),
        _=>throw new ArgumentOutOfRangeException(nameof(key))
    };
}
public sealed record MapQueryOperation<T>(MapQueryResult<T> Result,MapRetryHandle<T>? Retry);
public sealed record MapRetryStatus<T>(bool Active,bool Running,long Revision,MapQueryResult<T> Result);
public interface IMapRetryScheduler
{
    MapQueryOperation<T> Query<T>(string key,Func<MapRetryContext,MapQueryResult<T>> query,CancellationToken cancellationToken=default);
    MapRetryHandle<T> WaitFor<T>(string key,MissingMapObjects missing,T localValue,Func<MapRetryContext,MapQueryResult<T>> query);
}
public sealed class MapRetryHandle<T>:IDisposable
{
    private readonly Func<MapRetryStatus<T>> read;private readonly Action release;
    private CancellationTokenRegistration cancellation;private int disposed;
    internal MapRetryHandle(Func<MapRetryStatus<T>> read,Action release){this.read=read;this.release=release;}
    internal void CancelWith(CancellationToken token)=>cancellation=token.Register(Dispose);
    public MapRetryStatus<T> Capture()=>read();
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;release();cancellation.Dispose();}
}

