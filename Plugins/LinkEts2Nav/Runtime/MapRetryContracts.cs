using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Runtime;

public sealed record MapRetryContext(NearbyMapSnapshot Snapshot,long Revision);
public sealed record MapRetryStatus<T>(bool Active,bool Running,long Revision,MapQueryResult<T> Result);
public interface IMapRetryScheduler
{
    long ScopeRevision {get;}
    void Report(MapQueryIssue issue);
    MapRetryHandle<T> Register<T>(string key,MissingMapSectors missing,
        Func<MapRetryContext,CancellationToken,ValueTask<MapQueryResult<T>>> retry,CancellationToken cancellationToken=default);
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

