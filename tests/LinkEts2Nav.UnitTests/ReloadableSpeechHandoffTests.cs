using System.Collections.Concurrent;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
namespace LinkEts2Nav.UnitTests;

public sealed class ReloadableSpeechHandoffTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"speech-handoff-"+Guid.NewGuid().ToString("N"));
    private sealed class Worker(string name,ConcurrentQueue<string> order):ISpeechSynthesizer,ISpeechSynthesisDiagnostics
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationSeen=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowExit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DisposeEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowDispose=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Finish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation,Disposed;public int Active,DisposeCalls,Calls;public Exception? DisposeFailure;
        public bool? LastCacheHit=>true;
        public async Task<string> SynthesizeAsync(string text,CancellationToken token)
        {
            if(Disposed)throw new InvalidOperationException("A closed native worker was used.");
            Interlocked.Increment(ref Active);Interlocked.Increment(ref Calls);order.Enqueue("start-"+name);Started.TrySetResult();
            using var registration=token.Register(()=>{order.Enqueue("cancel-"+name);CancellationSeen.TrySetResult();});
            try{return IgnoreCancellation?await Finish.Task:await Finish.Task.WaitAsync(token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){await AllowExit.Task;throw;}
            finally{Interlocked.Decrement(ref Active);order.Enqueue("exit-"+name);}
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);order.Enqueue("dispose-"+name);
            if(Volatile.Read(ref Active)!=0)throw new InvalidOperationException("Closed native worker before its call exited.");
            DisposeEntered.TrySetResult();await AllowDispose.Task;
            if(DisposeFailure is {} failure)throw failure;Disposed=true;
        }
    }
    private async Task<string> Pack(string name,string text="fixed")
    {
        string path=Path.Combine(directory,name);
        await SpeechPackBuilder.BuildAsync(path,"model","voice",[text],(_,_)=>Task.FromResult(new byte[]{1,0,2,0}),cancellationToken:TestContext.Current.CancellationToken);
        return path;
    }
    private static async Task Await(Task task)=>await task.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken);
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while(!condition())await Task.Delay(5,timeout.Token);
    }
    private static Worker IdleWorker(string name,ConcurrentQueue<string> order)
    {var worker=new Worker(name,order);worker.AllowExit.SetResult();worker.AllowDispose.SetResult();return worker;}

    [Fact]
    public async Task HotSwapCancelsAndDrainsTheOldCallBeforeClosingAndStartingTheNewWorker()
    {
        string old=await Pack("old"),next=await Pack("new");await Pack(Path.Combine("new","CityNames"),"Paris");
        var order=new ConcurrentQueue<string>();var first=new Worker("old",order);first.AllowDispose.SetResult();var second=IdleWorker("new",order);
        var settings=new SpeechPackSettings(directory);int creates=0;
        var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old},settings,create:_=>Interlocked.Increment(ref creates)==1?first:second);
        await using var queue=new SpeechAnnouncementQueue(resources,_=>{},preGenerated:resources.Fixed);
        queue.UpdatePlan("navigation",["dynamic"]);await Await(first.Started.Task);var names=resources.Names;
        await settings.SaveAsync(next,TestContext.Current.CancellationToken);var switching=resources.ApplyPendingAsync(queue);
        await Await(first.CancellationSeen.Task);Assert.False(first.DisposeEntered.Task.IsCompleted);Assert.False(switching.IsCompleted);
        first.AllowExit.SetResult();Assert.True(await switching.WaitAsync(TimeSpan.FromSeconds(4),TestContext.Current.CancellationToken));
        await Await(second.Started.Task);Assert.True(first.Disposed);Assert.Equal(1,first.DisposeCalls);
        Assert.Same(names,resources.Names);Assert.Contains("Paris",names);Assert.Equal(next,settings.Read().ActiveDirectory);
        var events=order.ToArray();Assert.True(Array.IndexOf(events,"cancel-old")<Array.IndexOf(events,"exit-old"));
        Assert.True(Array.IndexOf(events,"exit-old")<Array.IndexOf(events,"dispose-old"));
        Assert.True(Array.IndexOf(events,"dispose-old")<Array.IndexOf(events,"start-new"));
        second.Finish.SetResult(settings.Pending!.Fixed.Values.Single());await Wait(()=>queue.IsReady("dynamic"));queue.ThrowIfFaulted();
    }

    [Fact]
    public async Task OldDisposalFailureIsNotLoggedAwayOrRetried()
    {
        string old=await Pack("old"),next=await Pack("new");var order=new ConcurrentQueue<string>();
        var failure=new IOException("native dispose defect");var first=IdleWorker("old",order);first.DisposeFailure=failure;int creates=0;
        var settings=new SpeechPackSettings(directory);
        var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old},settings,create:_=>{creates++;return first;});
        var queue=new SpeechAnnouncementQueue(resources,_=>{});await settings.SaveAsync(next,TestContext.Current.CancellationToken);
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>resources.ApplyPendingAsync(queue)));
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>resources.ApplyPendingAsync(queue)));
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>queue.DisposeAsync().AsTask()));
        Assert.Equal(1,creates);Assert.Equal(1,first.DisposeCalls);Assert.Null(settings.Read().ActiveDirectory);
    }

    [Fact]
    public async Task UnknownOldRequestFaultIsNotHiddenByTheReplacementGeneration()
    {
        string old=await Pack("old"),next=await Pack("new");var order=new ConcurrentQueue<string>();
        var first=new Worker("old",order){IgnoreCancellation=true};var second=IdleWorker("new",order);int creates=0;
        var settings=new SpeechPackSettings(directory);var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old},settings,
            create:_=>++creates==1?first:second);
        var queue=new SpeechAnnouncementQueue(resources,_=>{});queue.UpdatePlan("navigation",["dynamic"]);await Await(first.Started.Task);
        await settings.SaveAsync(next,TestContext.Current.CancellationToken);var switching=resources.ApplyPendingAsync(queue);
        await Await(first.CancellationSeen.Task);var failure=new IOException("old request is broken, not canceled");first.Finish.SetException(failure);
        await Await(first.DisposeEntered.Task);
        await Wait(()=>{try{queue.ThrowIfFaulted();return false;}catch(IOException error){Assert.Same(failure,error);return true;}});
        first.AllowDispose.SetResult();Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>switching));
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>queue.DisposeAsync().AsTask()));
        Assert.Equal(1,first.DisposeCalls);Assert.Equal(0,second.Calls);Assert.True(second.Disposed);
    }

    [Fact]
    public async Task PluginStopDuringHandoffRemainsNormalCancellationAndClosesBothWorkers()
    {
        string old=await Pack("old"),next=await Pack("new");var order=new ConcurrentQueue<string>();
        var first=new Worker("old",order);first.AllowDispose.SetResult();var second=IdleWorker("new",order);int creates=0;
        var settings=new SpeechPackSettings(directory);var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old},settings,
            create:_=>++creates==1?first:second);
        var queue=new SpeechAnnouncementQueue(resources,_=>{});queue.UpdatePlan("navigation",["dynamic"]);await Await(first.Started.Task);
        await settings.SaveAsync(next,TestContext.Current.CancellationToken);var switching=resources.ApplyPendingAsync(queue);
        await Await(first.CancellationSeen.Task);var stopping=queue.DisposeAsync().AsTask();first.AllowExit.SetResult();
        await Await(switching);await Await(stopping);queue.ThrowIfFaulted();
        Assert.Equal(1,first.DisposeCalls);Assert.Equal(1,second.DisposeCalls);Assert.Equal(0,second.Calls);Assert.Null(settings.Read().ActiveDirectory);
    }

    [Fact]
    public async Task NoPendingSelectionDoesNotReplaceTheWorker()
    {
        string old=await Pack("old");var order=new ConcurrentQueue<string>();var first=IdleWorker("old",order);
        var settings=new SpeechPackSettings(directory);int creates=0;
        var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old},settings,create:_=>{creates++;return first;});
        await using var queue=new SpeechAnnouncementQueue(resources,_=>{});
        Assert.False(await resources.ApplyPendingAsync(queue));Assert.Equal(1,creates);Assert.Equal(0,first.DisposeCalls);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
