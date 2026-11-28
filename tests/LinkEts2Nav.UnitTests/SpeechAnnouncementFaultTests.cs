using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class SpeechAnnouncementFaultTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"speech-fault-"+Guid.NewGuid().ToString("N"));
    private sealed class Synth(Func<string,CancellationToken,Task<string>> synthesize):ISpeechSynthesizer
    {
        public bool Disposed;public int Calls;
        public Task<string> SynthesizeAsync(string text,CancellationToken token){Calls++;return synthesize(text,token);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private string Audio(string name="speech")
    {Directory.CreateDirectory(directory);string path=Path.Combine(directory,name+".wav");File.WriteAllBytes(path,[1]);return path;}
    private static Task<string> FailAtSource(Exception failure)=>throw failure;
    private static async Task<T> Fault<T>(SpeechAnnouncementQueue queue)where T:Exception
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while(true)
        {
            try{queue.ThrowIfFaulted();}catch(T error){return error;}
            await Task.Delay(5,timeout.Token);
        }
    }
    [Fact]
    public async Task SynthesisFaultRemainsOriginalAndCannotBecomeACacheMissOrOrdinaryFailedText()
    {
        var failure=new IOException("worker protocol defect");var usage=new SpeechUsageCollector();
        var synth=new Synth((_,_)=>FailAtSource(failure));
        var queue=new SpeechAnnouncementQueue(synth,_=>{},usage:usage);queue.UpdatePlan("test",["text"]);
        Assert.Same(failure,await Fault<IOException>(queue));Assert.Equal(1,synth.Calls);
        Assert.Contains(nameof(FailAtSource),failure.StackTrace);
        Assert.Same(failure,Assert.Throws<IOException>(()=>queue.IsReady("text")));
        Assert.Same(failure,Assert.Throws<IOException>(()=>queue.TryGetAudio("text",out _)));
        Assert.Same(failure,Assert.Throws<IOException>(()=>queue.TryAnnounce("text")));
        Assert.Same(failure,Assert.Throws<IOException>(()=>queue.UpdatePlan("test",["text"])));
        Assert.Equal(1,Assert.Single(usage.Capture().Entries).PreparationFailureCount);
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>queue.DisposeAsync().AsTask()));Assert.True(synth.Disposed);
    }
    [Fact]
    public async Task SubmissionCallbackFailurePropagatesInsteadOfReturningFalse()
    {
        var failure=new InvalidOperationException("audio submission defect");
        await using var queue=new SpeechAnnouncementQueue(new Synth((_,_)=>throw new Exception("preloaded")),
            _=>throw failure,preGenerated:new Dictionary<string,string>{{"text",Audio()}});
        Assert.Same(failure,Assert.Throws<InvalidOperationException>(()=>queue.TryAnnounce("text")));
        queue.ThrowIfFaulted();
    }
    [Fact]
    public async Task SubmissionLogFailureIsNotTreatedAsAFailedSubmission()
    {
        int plays=0;var failure=new FormatException("log sink defect");
        await using var queue=new SpeechAnnouncementQueue(new Synth((_,_)=>throw new Exception("preloaded")),
            _=>plays++,_=>throw failure,preGenerated:new Dictionary<string,string>{{"text",Audio()}});
        Assert.Same(failure,Assert.Throws<FormatException>(()=>queue.TryAnnounce("text")));Assert.Equal(1,plays);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationLogFailureIsAnObservableWorkerFailure(bool beforeSynthesis)
    {
        var failure=new FormatException("preparation log defect");
        var synth=new Synth((_,_)=>Task.FromResult(Audio()));
        int expectedCalls=beforeSynthesis?0:1;
        var queue=new SpeechAnnouncementQueue(synth,_=>{},_=>{if(synth.Calls==expectedCalls)throw failure;});
        queue.UpdatePlan("test",["text"]);Assert.Same(failure,await Fault<FormatException>(queue));
        Assert.Equal(expectedCalls,synth.Calls);
        Assert.Same(failure,await Assert.ThrowsAsync<FormatException>(()=>queue.DisposeAsync().AsTask()));
    }
    [Fact]
    public async Task CacheChangedSubscriberFailureDoesNotBecomeAnOrdinaryFailedText()
    {
        var failure=new InvalidOperationException("cache subscriber defect");
        var queue=new SpeechAnnouncementQueue(new Synth((_,_)=>Task.FromResult(Audio())),_=>{});
        queue.CacheChanged+=()=>throw failure;queue.UpdatePlan("test",["text"]);
        Assert.Same(failure,await Fault<InvalidOperationException>(queue));
        Assert.Same(failure,Assert.Throws<InvalidOperationException>(()=>queue.TryGetAudio("text",out _)));
        Assert.Same(failure,await Assert.ThrowsAsync<InvalidOperationException>(()=>queue.DisposeAsync().AsTask()));
    }
    [Fact]
    public async Task RequestedStopAcceptsTheSynthesizersLinkedCancellationToken()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var usage=new SpeechUsageCollector();
        var synth=new Synth(async(_,token)=>
        {using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);started.SetResult();await Task.Delay(Timeout.Infinite,linked.Token);return "unused";});
        var queue=new SpeechAnnouncementQueue(synth,_=>{},usage:usage);queue.UpdatePlan("test",["text"]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        await queue.DisposeAsync();queue.ThrowIfFaulted();Assert.True(synth.Disposed);
        var row=Assert.Single(usage.Capture().Entries);Assert.Equal(1,row.PreparationCancelledCount);Assert.Equal(0,row.PreparationFailureCount);
    }
    [Fact]
    public async Task UnrequestedSynthesisCancellationIsObservableRatherThanAToneFallback()
    {
        var failure=new OperationCanceledException("worker deadline",new CancellationToken(true));
        var queue=new SpeechAnnouncementQueue(new Synth((_,_)=>throw failure),_=>{});queue.UpdatePlan("test",["text"]);
        Assert.Same(failure,await Fault<OperationCanceledException>(queue));
        Assert.Same(failure,await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queue.DisposeAsync().AsTask()));
    }
    [Fact]
    public async Task UnknownFaultFromASupersededPreparationIsNotDiscardedAsStaleSuccess()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synth=new Synth((_,_)=>{started.SetResult();return result.Task;});
        var queue=new SpeechAnnouncementQueue(synth,_=>{});queue.UpdatePlan("test",["text"]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
        queue.ReplacePreparedAudio(new Dictionary<string,string>(),()=>{});
        var failure=new IOException("old worker output closed");result.SetException(failure);
        Assert.Same(failure,await Fault<IOException>(queue));Assert.Equal(1,synth.Calls);
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>queue.DisposeAsync().AsTask()));
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
