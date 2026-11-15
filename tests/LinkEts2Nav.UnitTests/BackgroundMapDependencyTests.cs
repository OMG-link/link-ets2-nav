using System.Collections.Concurrent;
using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class BackgroundMapDependencyTests
{
    private static readonly SectorCoordinate Required=new(1,0);
    private static readonly SectorCoordinate Local=new(0,0);
    private static NearbyMapSnapshot Snapshot(TruckLib.ScsMap.Map map,bool ready)=>new(map,[],[],[Local,Required])
    {
        // Metadata can advertise a sector before its complete derived data exists.
        ReadySectors=ready?[Local,Required]:[Local],
        Coverage=MapScanCoverage.Create(Vector3.Zero,1000,null,null,0)
    };
    private static async Task WaitFor(Func<bool> test)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        while(!test())await Task.Delay(5,timeout.Token);
    }
    private sealed class Backend(Func<MapScanRequest,CancellationToken,NearbyMapSnapshot> read):IMapScanBackend
    {
        public NearbyMapSnapshot Read(Vector3 position,CancellationToken token)=>throw new InvalidOperationException("Request-aware backend required.");
        public NearbyMapSnapshot Read(MapScanRequest request,CancellationToken token)=>read(request,token);
        public void Dispose(){}
    }

    [Fact]
    public async Task RequiredSectorsLoadBeforeTheRetrySeesTheCompletePublishedSnapshot()
    {
        var initial=new TruckLib.ScsMap.Map();var complete=new TruckLib.ScsMap.Map();
        using var release=new ManualResetEventSlim();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen=new ConcurrentQueue<MapScanRequest>();
        await using var scanner=new BackgroundMapScanner(_=>new Backend((request,token)=>
        {
            seen.Enqueue(request);
            if(!request.RequiredSectors.Contains(Required))return Snapshot(initial,false);
            started.TrySetResult();release.Wait(token);return Snapshot(complete,true);
        }),TestContext.Current.CancellationToken);
        scanner.Request(new("resource","session","unused",[],Vector3.Zero),DateTimeOffset.UtcNow);
        await WaitFor(()=>scanner.Capture().Map is not null);
        int invoked=0;
        var missing=new MissingMapSectors([Required],"required native connection"){QueryAnchor=Vector3.Zero};
        using var handle=scanner.Retries.Register("derived-connection",missing,(context,token)=>
        {
            Interlocked.Increment(ref invoked);
            Assert.Same(complete,context.Snapshot.Map);
            Assert.Same(context.Snapshot,scanner.Capture().Map);
            return ValueTask.FromResult(MapQueryResult<int>.Ready(17));
        },TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(6),TestContext.Current.CancellationToken);
            Assert.Contains(seen,r=>r.RequiredSectors.Contains(Required));
            Assert.Same(initial,scanner.Capture().Map!.Map);
            Assert.Equal(0,Volatile.Read(ref invoked));
            Assert.False(handle.Capture().Result.IsSuccess);
        }
        finally{release.Set();}
        await WaitFor(()=>handle.Capture().Result.IsSuccess);
        Assert.Equal(17,handle.Capture().Result.Value);
        Assert.Equal(1,Volatile.Read(ref invoked));
        Assert.Equal(scanner.Capture().Revision,handle.Capture().Revision);
    }

    [Fact]
    public async Task FailedDependencyReadDoesNotPublishReadyOrWakeTheRetry()
    {
        var initial=new TruckLib.ScsMap.Map();
        var seen=new ConcurrentQueue<MapScanRequest>();
        await using var scanner=new BackgroundMapScanner(_=>new Backend((request,token)=>
        {
            seen.Enqueue(request);
            if(request.RequiredSectors.Contains(Required))throw new IOException("sector read failed");
            return Snapshot(initial,false);
        }),TestContext.Current.CancellationToken);
        scanner.Request(new("resource","session","unused",[],Vector3.Zero),DateTimeOffset.UtcNow);
        await WaitFor(()=>scanner.Capture().Map is not null);
        int invoked=0;
        using var handle=scanner.Retries.Register("failed-native-connection",
            new MissingMapSectors([Required],"unreadable native connection"){QueryAnchor=Vector3.Zero},
            (context,token)=>{Interlocked.Increment(ref invoked);return ValueTask.FromResult(MapQueryResult<int>.Ready(17));},
            TestContext.Current.CancellationToken);
        await WaitFor(()=>scanner.Capture().Error is not null);
        Assert.Contains(seen,r=>r.RequiredSectors.Contains(Required));
        Assert.Same(initial,scanner.Capture().Map!.Map);
        Assert.Equal(0,Volatile.Read(ref invoked));
        Assert.False(handle.Capture().Result.IsSuccess);
        Assert.DoesNotContain(Required,scanner.Capture().Map!.ReadySectors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalRouteProgressKeepsPendingDependencyWhileReplacementCancelsIt(bool replacement)
    {
        var initial=new TruckLib.ScsMap.Map();var complete=new TruckLib.ScsMap.Map();
        using var release=new ManualResetEventSlim();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scanner=new BackgroundMapScanner(_=>new Backend((request,token)=>
        {
            if(!request.RequiredSectors.Contains(Required))return Snapshot(initial,false);
            started.TrySetResult();release.Wait(token);return Snapshot(complete,true);
        }),TestContext.Current.CancellationToken);
        var now=DateTimeOffset.UtcNow;
        var route=new RouteSnapshot([new(1,0,0),new(2,0,0),new(3,0,0)],"initial",RouteAvailability.Available,now);
        var request=new MapScanRequest("resource","session","unused",[],Vector3.Zero){Route=route};
        scanner.Request(request,now);
        await WaitFor(()=>scanner.Capture().Map is not null);
        int invoked=0;
        using var handle=scanner.Retries.Register("progress",new MissingMapSectors([Required],"progress dependency"){QueryAnchor=Vector3.Zero},
            (context,token)=>{Interlocked.Increment(ref invoked);return ValueTask.FromResult(MapQueryResult<int>.Ready(1));},TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(6),TestContext.Current.CancellationToken);
            var updated=route with{Revision="updated",Nodes=replacement?[new(9,0,0),new(10,0,0)]:[new(2,0,0),new(3,0,0),new(4,0,0)]};
            Assert.True(scanner.Request(request with{Route=updated},now));
            Assert.Equal(!replacement,handle.Capture().Active);
        }
        finally{release.Set();}
        if(replacement)
        {
            await WaitFor(()=>!scanner.Capture().IsScanning);
            Assert.False(handle.Capture().Active);Assert.Equal(0,Volatile.Read(ref invoked));
        }
        else
        {
            await WaitFor(()=>Volatile.Read(ref invoked)>0);
            Assert.True(handle.Capture().Active);
            Assert.Contains(Required,scanner.Capture().Map!.ReadySectors);
        }
    }
}
