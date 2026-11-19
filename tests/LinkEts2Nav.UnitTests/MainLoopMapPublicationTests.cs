using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class MainLoopMapPublicationTests
{
    private sealed class Backend(Func<Vector3,CancellationToken,NearbyMapSnapshot> read):IMapScanBackend
    {public NearbyMapSnapshot Read(Vector3 position,CancellationToken token)=>read(position,token);public void Dispose(){}}
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while(!condition())await Task.Delay(5,timeout.Token);
    }
    [Fact]
    public async Task AWaitRegisteredAfterTheBackgroundWindowWasQueuedStillRunsAfterMainLoopAdoption()
    {
        var a=new NearbyMapSnapshot(new(),[],[],[]);var map=new TruckLib.ScsMap.Map();map.MapItems.Add(99,new Road{Uid=99});var b=new NearbyMapSnapshot(map,[],[],[]);
        int reads=0;
        await using var scanner=new BackgroundMapScanner(_=>new Backend((position,_)=>{Interlocked.Increment(ref reads);return position.X<25?a:b;}));
        var now=DateTimeOffset.UtcNow;scanner.Request(new("r","s","",[],Vector3.Zero),now);
        await Wait(()=>!scanner.Capture().IsScanning);Assert.Null(scanner.Capture().Map);scanner.AdoptReady();Assert.Same(a.Map,scanner.Capture().Map!.Map);
        scanner.Request(new("r","s","",[],new(50,0,0)),now.AddSeconds(1));await Wait(()=>!scanner.Capture().IsScanning);
        Assert.Same(a.Map,scanner.Capture().Map!.Map);int callbacks=0;int mainThread=Environment.CurrentManagedThreadId;
        using var retry=scanner.Retries.Query("late",context=>
        {
            if(!context.Contains(new(MapObjectKind.Item,99)))return MapQueryResult<int>.Unavailable(new MissingMapObjects([new(MapObjectKind.Item,99)],"late"));
            Assert.Equal(mainThread,Environment.CurrentManagedThreadId);Assert.Same(b.Map,scanner.Capture().Map!.Map);callbacks++;return MapQueryResult<int>.Ready(9);
        }).Retry!;
        Assert.Equal(0,callbacks);scanner.AdoptReady();Assert.Equal(1,callbacks);Assert.True(retry.Capture().Result.IsSuccess);
        Assert.False(scanner.Request(new("r","s","",[],new(50,0,0)),now.AddSeconds(10)));Assert.Equal(2,reads);
    }
    [Fact]
    public async Task ReturningToTheWindowAlreadyBeingReadCancelsTheSupersededQueuedPlacement()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int reads=0;
        await using var scanner=new BackgroundMapScanner(_=>new Backend((_,token)=>
        {Interlocked.Increment(ref reads);entered.Set();release.Wait(token);return new(new(),[],[],[]);}));
        var now=DateTimeOffset.UtcNow;scanner.Request(new("r","s","",[],Vector3.Zero),now);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken));
        try
        {
            Assert.True(scanner.Request(new("r","s","",[],new(100,0,0)),now.AddSeconds(1)));
            Assert.False(scanner.Request(new("r","s","",[],Vector3.Zero),now.AddSeconds(2)));
        }
        finally{release.Set();}
        await Wait(()=>!scanner.Capture().IsScanning);scanner.AdoptReady();Assert.Equal(1,reads);
    }
    [Fact]
    public async Task AQueuedNearbyWindowSurvivesTheFollowingRouteScanIoFailureWithoutHidingTheError()
    {
        var nearby=new NearbyMapSnapshot(new(),[],[],[]);int reads=0;
        await using var scanner=new BackgroundMapScanner(_=>new Backend((_,_)=>Interlocked.Increment(ref reads)==1?nearby:throw new IOException("route file unavailable")));
        var route=RouteSnapshot.Parse([new(10,100,0),new(20,0,0)],DateTimeOffset.UtcNow);
        scanner.Request(new("r","s","",[],Vector3.Zero){Route=route},DateTimeOffset.UtcNow);
        await Wait(()=>scanner.Capture().Error is not null);
        var adopted=scanner.AdoptReady();Assert.Same(nearby.Map,adopted.Map!.Map);Assert.Contains("route file unavailable",adopted.Error);
    }
    [Fact]
    public async Task ARouteChangeCancelsOldWaitsWithoutRequestingMoreDiskWork()
    {
        int reads=0;await using var scanner=new BackgroundMapScanner(_=>new Backend((_,_)=>{reads++;return new(new(),[],[],[]);}));
        scanner.Request(new("r","s","",[],Vector3.Zero),DateTimeOffset.UtcNow);await Wait(()=>!scanner.Capture().IsScanning);scanner.AdoptReady();
        using var lease=scanner.Retries.Query<int>("old",_=>MapQueryResult<int>.Unavailable(new MissingMapObjects([new(MapObjectKind.Node,123)],"old route"))).Retry!;
        scanner.ObserveRoute(RouteSnapshot.Parse([new(500,100,0),new(600,0,0)],DateTimeOffset.UtcNow));
        Assert.False(lease.Capture().Active);Assert.Equal(1,reads);Assert.NotNull(scanner.Capture().Map);
    }
    [Fact]
    public async Task AWindowCompletedDuringCallbacksIsLeftForTheNextBoundedAdoption()
    {
        var initial=new NearbyMapSnapshot(new(),[],[],[]);var aMap=new TruckLib.ScsMap.Map();aMap.MapItems.Add(99,new Road{Uid=99});
        var a=new NearbyMapSnapshot(aMap,[],[],[]);var b=new NearbyMapSnapshot(new(),[],[],[]);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var scanner=new BackgroundMapScanner(_=>new Backend((position,token)=>
        {if(position.X<25)return initial;if(position.X<75)return a;entered.Set();release.Wait(token);return b;}));
        var now=DateTimeOffset.UtcNow;scanner.Request(new("r","s","",[],Vector3.Zero),now);await Wait(()=>!scanner.Capture().IsScanning);scanner.AdoptReady();
        using var lease=scanner.Retries.Query<int>("bounded",context=>
        {
            if(!context.Contains(new(MapObjectKind.Item,99)))return MapQueryResult<int>.Unavailable(new MissingMapObjects([new(MapObjectKind.Item,99)],"bounded"));
            release.Set();Assert.True(SpinWait.SpinUntil(()=>!scanner.Capture().IsScanning,TimeSpan.FromSeconds(4)));return MapQueryResult<int>.Ready(1);
        }).Retry!;
        scanner.Request(new("r","s","",[],new(50,0,0)),now.AddSeconds(1));await Wait(()=>!scanner.Capture().IsScanning);
        scanner.Request(new("r","s","",[],new(100,0,0)),now.AddSeconds(2));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken));
        try{Assert.Same(a.Map,scanner.AdoptReady().Map!.Map);Assert.Same(b.Map,scanner.AdoptReady().Map!.Map);}
        finally{release.Set();}
    }
}
