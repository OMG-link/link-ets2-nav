using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class ScannerLifecycleReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackCanWaitForAnotherThreadToUseScanner(bool nestedInvalidate)
    {
        var a=new SectorCoordinate(0,0);
        var snapshot=new NearbyMapSnapshot(new(),[],[],[a])
        {ReadySectors=[a],Coverage=MapScanCoverage.Create(Vector3.Zero,1100,null,null,0)};
        using var releaseReads=new ManualResetEventSlim();
        await using var scanner=new BackgroundMapScanner(_=>new Backend(snapshot,releaseReads));
        var now=DateTimeOffset.UtcNow;
        scanner.Request(new("resource","session","unused",[],Vector3.Zero),now);
        await Wait(()=>scanner.Capture().Map is not null);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellationCalls=0;bool nestedCompletedBeforeCallbackReturn=false;
        using var handle=scanner.Retries.Register("cancel-reentry",new MissingMapSectors([a],"known") {QueryAnchor=Vector3.Zero},async(c,t)=>
        {
            using var registration=t.Register(()=>
            {
                Interlocked.Increment(ref cancellationCalls);
                var thread=new Thread(()=>
                {
                    try{
                        if(nestedInvalidate)scanner.Invalidate();
                        else scanner.Request(new("resource","nested-session","unused",[],Vector3.Zero),now.AddSeconds(1));
                        nested.TrySetResult();
                    }catch(Exception ex){nested.TrySetException(ex);}
                });
                thread.Start();nestedCompletedBeforeCallbackReturn=nested.Task.Wait(TimeSpan.FromMilliseconds(400));
            });
            started.TrySetResult();await release.Task;return MapQueryResult<int>.Ready(1);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        try
        {
            scanner.Invalidate();
            await nested.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        }
        finally{release.TrySetResult();releaseReads.Set();}
        Assert.Equal(1,cancellationCalls);
        Assert.True(nestedCompletedBeforeCallbackReturn,"Cancellation callback ran under the scanner lifecycle lock, preventing legal cross-thread reentry.");
    }
    private static async Task Wait(Func<bool> predicate)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);stop.CancelAfter(TimeSpan.FromSeconds(4));
        while(!predicate())await Task.Delay(5,stop.Token);
    }
    private sealed class Backend(NearbyMapSnapshot snapshot,ManualResetEventSlim release):IMapScanBackend
    {
        private int count;
        public NearbyMapSnapshot Read(Vector3 p,CancellationToken cancellationToken)
        {if(Interlocked.Increment(ref count)>1)release.Wait(cancellationToken);return snapshot;}
        public void Dispose(){}
    }
}
