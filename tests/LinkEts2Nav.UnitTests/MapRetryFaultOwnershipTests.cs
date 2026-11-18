using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class MapRetryFaultOwnershipTests
{
    private static readonly SectorCoordinate A=new(0,0);
    private static MissingMapSectors Missing()=>new([A],"local"){QueryAnchor=Vector3.Zero};
    private static NearbyMapSnapshot Snapshot()=>new(new(),[],[],[A])
        {ReadySectors=[A],Coverage=MapScanCoverage.Create(Vector3.Zero,1100,null,null,0)};
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while(!condition())await Task.Delay(5,timeout.Token);
    }
    private static async Task Barrier(MapRetryCoordinator owner)
    {
        owner.Publish(Snapshot(),2);
        using var handle=owner.Register("barrier",Missing(),(_,_)=>ValueTask.FromResult(MapQueryResult<int>.Ready(2)));
        await Wait(()=>handle.Capture().Result.IsSuccess);
    }
    [Fact]
    public async Task UnknownQueryFaultRemainsTheOriginalExceptionRatherThanInvalidData()
    {
        var failure=new InvalidOperationException("native-query-defect");
        var owner=new MapRetryCoordinator(_=>{});owner.Publish(Snapshot(),1);
        using var handle=owner.Register<int>("bad",Missing(),(_,_)=>throw failure);
        await Wait(()=>{try{owner.ThrowIfFaulted();return false;}catch(InvalidOperationException error){Assert.Same(failure,error);return true;}});
        Assert.Same(failure,Assert.Throws<InvalidOperationException>(owner.ThrowIfFaulted));
        Assert.IsType<MissingMapSectors>(handle.Capture().Result.Issue);
        Assert.Same(failure,await Assert.ThrowsAsync<InvalidOperationException>(()=>owner.DisposeAsync().AsTask()));
    }
    [Fact]
    public async Task RequestDefectPropagatesWithoutTryingASecondaryLogFallback()
    {
        int logCalls=0;var failure=new FormatException("demand-callback-defect");
        var owner=new MapRetryCoordinator(_=>throw failure,_=>{logCalls++;throw new InvalidOperationException("secondary-log");});
        owner.Publish(Snapshot(),1);
        await Wait(()=>{try{owner.ThrowIfFaulted();return false;}catch(FormatException error){Assert.Same(failure,error);return true;}});
        Assert.Same(failure,Assert.Throws<FormatException>(owner.ThrowIfFaulted));Assert.Equal(0,logCalls);
        Assert.Same(failure,await Assert.ThrowsAsync<FormatException>(()=>owner.DisposeAsync().AsTask()));
    }
    [Fact]
    public async Task DeferredCancelOwnsTheSourceAfterTheQueryHasFinished()
    {
        await using var owner=new MapRetryCoordinator(_=>{});owner.Publish(Snapshot(),1);
        var started=new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int callbacks=0;
        CancellationTokenRegistration registration=default;
        using var handle=owner.Register("deferred",Missing(),async(_,token)=>
        {
            registration=token.Register(()=>Interlocked.Increment(ref callbacks));started.SetResult(token);
            await finish.Task;return MapQueryResult<int>.Ready(1);
        });
        var token=await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        var cancel=owner.ResetScope();finish.SetResult();
        await Barrier(owner); // The single query worker has released its old ownership.
        Assert.False(token.IsCancellationRequested);
        _=token.WaitHandle; // A deferred cancel reservation still owns a live source.
        cancel();Assert.True(token.IsCancellationRequested);Assert.Equal(1,callbacks);
        Assert.Throws<ObjectDisposedException>(()=>token.WaitHandle);
        registration.Dispose();
    }
    [Fact]
    public async Task CancellationCallbackFaultIsNotSwallowedAndStillReleasesTheSource()
    {
        await using var owner=new MapRetryCoordinator(_=>{});owner.Publish(Snapshot(),1);
        var started=new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure=new InvalidOperationException("cancel-callback-defect");CancellationTokenRegistration registration=default;
        using var handle=owner.Register("callback",Missing(),async(_,token)=>
        {
            registration=token.Register(()=>throw failure);started.SetResult(token);
            await Task.Delay(Timeout.Infinite,token);return MapQueryResult<int>.Ready(1);
        });
        var token=await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Contains(failure,Assert.Throws<AggregateException>(owner.Reset).InnerExceptions);
        await Barrier(owner);owner.ThrowIfFaulted();
        Assert.Throws<ObjectDisposedException>(()=>token.WaitHandle);registration.Dispose();
    }
    [Fact]
    public async Task ReleasingAnActiveLeaseCancelsNormallyAndAllowsTheNextQuery()
    {
        await using var owner=new MapRetryCoordinator(_=>{});owner.Publish(Snapshot(),1);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle=owner.Register("cancel",Missing(),async(_,token)=>
        {started.SetResult();await Task.Delay(Timeout.Infinite,token);return MapQueryResult<int>.Ready(1);});
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        handle.Dispose();await Barrier(owner);owner.ThrowIfFaulted();
    }
    [Fact]
    public async Task UnrequestedCancellationIsObservedEvenWhenTheTaskIsCanceledRatherThanFaulted()
    {
        var owner=new MapRetryCoordinator(_=>{});owner.Publish(Snapshot(),1);
        using var handle=owner.Register<int>("unrelated-cancel",Missing(),(_,_)=>throw new OperationCanceledException("not-requested"));
        await Wait(()=>{try{owner.ThrowIfFaulted();return false;}catch(OperationCanceledException){return true;}});
        Assert.Throws<OperationCanceledException>(owner.ThrowIfFaulted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>owner.DisposeAsync().AsTask());
    }
}
