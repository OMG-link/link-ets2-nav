using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class GuidancePlaybackFaultTests
{
    private sealed class Player(Func<int,CancellationToken,Task> play):IGuidanceAudioPlayer
    {
        public int Calls;
        public Task PlayAsync(string _,CancellationToken token)=>play(Interlocked.Increment(ref Calls),token);
    }
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while(!condition())await Task.Delay(5,timeout.Token);
    }
    private static void Enqueue(GuidancePlaybackManager manager)=>manager.Notify("test",new("one","test speech"));
    [Fact]
    public async Task ActualPlayerTimeoutRetainsTheCueAndRetriesAfterDeviceRecovery()
    {
        var played=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((call,_)=>call==1?Task.FromException(new TimeoutException("host completion missing")):Task.CompletedTask);
        await using var manager=new GuidancePlaybackManager(null,player,()=>"tone.wav");
        manager.Played+=_=>played.TrySetResult();Enqueue(manager);
        await played.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Equal(2,player.Calls);manager.ThrowIfFaulted();Assert.Empty(manager.Pending);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerProgrammingFaultOrMissingAssetPropagatesWithoutRetry(bool missing)
    {
        Exception failure=missing?new FileNotFoundException("selected WAV disappeared","speech.wav"):
            new InvalidOperationException("player programming defect");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((_,_)=>{started.SetResult();return Task.FromException(failure);});
        var manager=new GuidancePlaybackManager(null,player,()=>"speech.wav");Enqueue(manager);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Same(failure,await Assert.ThrowsAsync(failure.GetType(),()=>manager.DisposeAsync().AsTask()));
        Assert.Equal(1,player.Calls);Assert.Empty(manager.Pending);
        Assert.Same(failure,Assert.Throws(failure.GetType(),manager.ThrowIfFaulted));
    }
    [Fact]
    public async Task CompletionSubscriberTimeoutIsNotAPlaybackTimeoutAndIsNotRetried()
    {
        var failure=new TimeoutException("subscriber defect");
        var called=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((_,_)=>Task.CompletedTask);
        var manager=new GuidancePlaybackManager(null,player,()=>"tone.wav");
        manager.Played+=_=>{called.SetResult();throw failure;};Enqueue(manager);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Same(failure,await Assert.ThrowsAsync<TimeoutException>(()=>manager.DisposeAsync().AsTask()));
        Assert.Equal(1,player.Calls);Assert.Empty(manager.Pending);
    }
    [Fact]
    public async Task CompletionLogTimeoutIsNotAPlaybackTimeoutAndIsNotRetried()
    {
        var failure=new TimeoutException("log sink defect");
        var called=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((_,_)=>Task.CompletedTask);
        var manager=new GuidancePlaybackManager(null,player,()=>"tone.wav",_=>{called.SetResult();throw failure;});Enqueue(manager);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Same(failure,await Assert.ThrowsAsync<TimeoutException>(()=>manager.DisposeAsync().AsTask()));
        Assert.Equal(1,player.Calls);
    }
    [Fact]
    public async Task FallbackResolverTimeoutIsNotAPlaybackTimeout()
    {
        var failure=new TimeoutException("fallback resolver defect");
        var called=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((_,_)=>Task.CompletedTask);
        var manager=new GuidancePlaybackManager(null,player,()=>{called.SetResult();throw failure;});Enqueue(manager);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        Assert.Same(failure,await Assert.ThrowsAsync<TimeoutException>(()=>manager.DisposeAsync().AsTask()));
        Assert.Equal(0,player.Calls);
    }
    [Fact]
    public async Task UnrequestedPlayerCancellationRemainsObservable()
    {
        var player=new Player((_,_)=>Task.FromCanceled(new CancellationToken(true)));
        var manager=new GuidancePlaybackManager(null,player,()=>"tone.wav");Enqueue(manager);
        await Wait(()=>{try{manager.ThrowIfFaulted();return false;}catch(OperationCanceledException){return true;}});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.DisposeAsync().AsTask());Assert.Equal(1,player.Calls);
    }
    [Fact]
    public async Task RequestedDisposalCancelsActivePlaybackNormally()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player=new Player((_,token)=>{started.SetResult();return Task.Delay(Timeout.Infinite,token);});
        var manager=new GuidancePlaybackManager(null,player,()=>"tone.wav");Enqueue(manager);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        await manager.DisposeAsync();manager.ThrowIfFaulted();
    }
}
