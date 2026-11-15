using System.Collections.Concurrent;
using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class MapRetryCoordinatorTests
{
    private static readonly SectorCoordinate A=new(4,0),B=new(5,0);
    private static MissingMapSectors Missing(params SectorCoordinate[] sectors)=>new(sectors,"same diagnostic") {QueryAnchor=Vector3.Zero};
    private static NearbyMapSnapshot Snapshot(params SectorCoordinate[] ready)=>new(new(),[],[],ready)
    {ReadySectors=ready,Coverage=MapScanCoverage.Create(Vector3.Zero,1100,null,null,0)};
    private static async Task Wait(Func<bool> condition)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(4));
        while(!condition())await Task.Delay(5,stop.Token);
    }
    [Fact]
    public async Task RegisterAfterPublicationRunsOnceAndSuccessCanBeDisposedAndReset()
    {
        await using var coordinator=new MapRetryCoordinator(_=>{});
        coordinator.Publish(Snapshot(A),1);
        int calls=0;
        using(var handle=coordinator.Register("one",Missing(A),(c,t)=>
            {Interlocked.Increment(ref calls);return ValueTask.FromResult(MapQueryResult<int>.Ready(42));}))
        {
            await Wait(()=>handle.Capture().Result.IsSuccess);
            Assert.Equal(42,handle.Capture().Result.Value);
            Assert.Equal(1,calls);
        }
        coordinator.Reset();
    }
    [Fact]
    public async Task AllDependenciesMustBeInSamePublishedSnapshot()
    {
        await using var coordinator=new MapRetryCoordinator(_=>{});
        coordinator.Publish(Snapshot(A),1);int calls=0;
        using var handle=coordinator.Register("both",Missing(A,B),(c,t)=>
            {Interlocked.Increment(ref calls);return ValueTask.FromResult(MapQueryResult<int>.Ready(42));});
        coordinator.Publish(Snapshot(B),2);await Task.Delay(30,TestContext.Current.CancellationToken);Assert.Equal(0,calls);
        coordinator.Publish(Snapshot(A,B),3);await Wait(()=>handle.Capture().Result.IsSuccess);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task SameHumanReasonDoesNotOverwriteDistinctSectorDemands()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        coordinator.Publish(Snapshot(),1);
        coordinator.Report(Missing(A));coordinator.Report(Missing(B));
        await Wait(()=>requests.TryPeek(out _)&&requests.Last().Contains(A)&&requests.Last().Contains(B));
        var latest=requests.Last();Assert.Contains(A,latest);Assert.Contains(B,latest);
    }
    [Fact]
    public async Task SatisfiedReportRemainsInSameScopeToPreventWindowOscillation()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        coordinator.Publish(Snapshot(),1);coordinator.Report(Missing(A));
        await Wait(()=>requests.TryPeek(out _)&&requests.Last().Contains(A));
        int before=requests.Count;
        coordinator.Publish(Snapshot(A),2);
        await Wait(()=>requests.Count>before);
        Assert.Contains(A,requests.Last());
    }
    [Fact]
    public async Task LateOldQueryCannotOverwriteNewPublication()
    {
        await using var coordinator=new MapRetryCoordinator(_=>{});
        coordinator.Publish(Snapshot(A),1);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle=coordinator.Register("race",Missing(A),async(c,t)=>
        {
            if(c.Revision==1){started.TrySetResult();await release.Task;}
            return MapQueryResult<long>.Ready(c.Revision);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        coordinator.Publish(Snapshot(A),2);release.TrySetResult();
        await Wait(()=>handle.Capture().Result.IsSuccess);
        Assert.Equal(2,handle.Capture().Result.Value);
    }
    [Fact]
    public async Task SameQueryKeyCanBeRegisteredForANewerPublishedSnapshot()
    {
        await using var coordinator=new MapRetryCoordinator(_=>{});
        coordinator.Publish(Snapshot(A),1);
        using var old=coordinator.Register("same-key",Missing(A),(c,t)=>ValueTask.FromResult(MapQueryResult<long>.Ready(c.Revision)));
        await Wait(()=>old.Capture().Result.IsSuccess);
        coordinator.Publish(Snapshot(A),2);
        using var next=coordinator.Register("same-key",Missing(A),(c,t)=>ValueTask.FromResult(MapQueryResult<long>.Ready(c.Revision)));
        await Wait(()=>next.Capture().Result.IsSuccess);
        Assert.Equal(2,next.Capture().Result.Value);
    }
    [Fact]
    public async Task AlreadyCancelledRegistrationCannotLeaveADemandBehind()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        coordinator.Publish(Snapshot(),1);
        await Wait(()=>requests.Count>0);int before=requests.Count;
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        using var handle=coordinator.Register("cancelled",Missing(A),(c,t)=>ValueTask.FromResult(MapQueryResult<int>.Ready(1)),cancelled.Token);
        Assert.False(handle.Capture().Active);
        await Wait(()=>requests.Count>before&&!requests.Last().Contains(A));
        Assert.DoesNotContain(A,requests.Last());
    }
    [Fact]
    public async Task ExtraSectorDoesNotBecomeALocalQueryScope()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        var coverage=MapScanCoverage.Create(Vector3.Zero,1100,null,null,0).WithAdditionalSectors([A]);
        coordinator.Publish(Snapshot(A) with{Coverage=coverage},1);
        await Wait(()=>requests.Count>0);
        var remote=new MissingMapSectors([B],"remote child") {QueryAnchor=new(17000,0,0)};
        coordinator.Report(remote);
        Assert.DoesNotContain(B,requests.Last());
    }
    [Fact]
    public async Task ResetWinsOverAnOlderBlockedDemandPublication()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        await using var coordinator=new MapRetryCoordinator(s=>
        {
            if(s.Contains(A)){entered.Set();release.Wait(TestContext.Current.CancellationToken);}
            requests.Enqueue(s.ToArray());
        });
        coordinator.Publish(Snapshot(),1);
        await Wait(()=>requests.Count>0);int before=requests.Count;
        var report=Task.Run(()=>coordinator.Report(Missing(A)),TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken));
        try{coordinator.Reset();}finally{release.Set();}
        await report.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        await Wait(()=>requests.Count>=before+2&&!requests.Last().Contains(A));
    }
    [Fact]
    public async Task NotificationFailureDoesNotKillLaterDemandPublication()
    {
        int calls=0;var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>
        {if(Interlocked.Increment(ref calls)==1)throw new IOException("expected consumer failure");requests.Enqueue(s.ToArray());});
        coordinator.Publish(Snapshot(),1);await Wait(()=>Volatile.Read(ref calls)>0);
        coordinator.Report(Missing(A));await Wait(()=>requests.Count>0&&requests.Last().Contains(A));
    }
    [Fact]
    public async Task SlowQueryDoesNotBlockLoadingOrDisposalClearingDemands()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        coordinator.Publish(Snapshot(A),1);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle=coordinator.Register("slow",Missing(A),async(c,t)=>
        {started.TrySetResult();await release.Task;return MapQueryResult<int>.Ready(1);});
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        try{coordinator.Report(Missing(B));await Wait(()=>requests.Count>0&&requests.Last().Contains(B));}
        finally{release.TrySetResult();await coordinator.DisposeAsync();}
        Assert.Empty(requests.Last());
    }
    [Fact]
    public async Task StaleLoadGenerationCannotPublishAfterReset()
    {
        await using var coordinator=new MapRetryCoordinator(_=>{});
        long old=coordinator.Generation;
        coordinator.Reset();long scope=coordinator.ScopeRevision;
        Assert.False(coordinator.Publish(Snapshot(A),10,old));
        Assert.Equal(scope,coordinator.ScopeRevision);
        Assert.True(coordinator.Publish(Snapshot(A),11,coordinator.Generation));
        using var handle=coordinator.Register("current-only",Missing(A),(c,t)=>ValueTask.FromResult(MapQueryResult<long>.Ready(c.Revision)));
        await Wait(()=>handle.Capture().Result.IsSuccess);
        Assert.Equal(11,handle.Capture().Result.Value);
    }
    [Fact]
    public async Task AcceptedSnapshotCannotReportOldIssuesAfterALaterReset()
    {
        var requests=new ConcurrentQueue<SectorCoordinate[]>();
        await using var coordinator=new MapRetryCoordinator(s=>requests.Enqueue(s.ToArray()));
        long old=coordinator.Generation;
        Assert.True(coordinator.Publish(Snapshot(),1,old));
        coordinator.Reset();
        Assert.True(coordinator.Publish(Snapshot(),2,coordinator.Generation));
        await Wait(()=>requests.Count>0);int before=requests.Count;
        coordinator.Report(Missing(A),old);
        // A fresh request is a barrier after the rejected old report.
        coordinator.Report(Missing(B),coordinator.Generation);
        await Wait(()=>requests.Count>before&&requests.Last().Contains(B));
        Assert.DoesNotContain(A,requests.Last());
    }

    [Fact]
    public async Task DistantOwnerIsDiagnosedWithoutTruncatingALocalDependency()
    {
        var messages=new System.Collections.Concurrent.ConcurrentQueue<string>();
        var requested=new System.Collections.Concurrent.ConcurrentQueue<IReadOnlyList<TruckLib.ScsMap.SectorCoordinate>>();
        await using var coordinator=new MapRetryCoordinator(sectors=>requested.Enqueue(sectors),messages.Enqueue);
        var distant=new TruckLib.ScsMap.SectorCoordinate(5,0);
        var map=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[])
        {Coverage=MapScanCoverage.Create(System.Numerics.Vector3.Zero,1000,null,null,0)};
        coordinator.Publish(map,1);
        coordinator.Report(new MissingMapSectors([distant],"runtime native owner"){QueryAnchor=System.Numerics.Vector3.Zero});
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while(!requested.Any(sectors=>sectors.Contains(distant)))await Task.Delay(5,timeout.Token);
        Assert.Contains(messages,message=>message.Contains("exceeds 10km")&&message.Contains("not a measured geometry distance"));
    }
}

