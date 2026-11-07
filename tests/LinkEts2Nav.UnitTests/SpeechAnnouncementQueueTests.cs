using System.Threading.Channels;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class SpeechAnnouncementQueueTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"speech-queue-"+Guid.NewGuid().ToString("N"));
    private sealed class Synth : ISpeechSynthesizer
    {
        public Channel<(string Text,TaskCompletionSource<string> Result)> Requests {get;}=Channel.CreateUnbounded<(string,TaskCompletionSource<string>)>();
        public bool Disposed {get;private set;}
        public async Task<string> SynthesizeAsync(string text,CancellationToken token)
        {
            var done=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Requests.Writer.WriteAsync((text,done),token);
            return await done.Task.WaitAsync(token);
        }
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private string File(string name)
    {
        Directory.CreateDirectory(root);
        string path=Path.Combine(root,name+".wav");
        System.IO.File.WriteAllBytes(path,[1]);
        return path;
    }
    [Fact]
    public async Task ClearingOnePlanPreservesOtherChannelAndSynthesisNeverAutoplays()
    {
        var synth=new Synth();
        var ready=Channel.CreateUnbounded<bool>();
        var played=new List<string>();
        await using var queue=new SpeechAnnouncementQueue(synth,played.Add,
            m=>{if(m.StartsWith("TTS 文件缓存已就绪"))ready.Writer.TryWrite(true);});
        queue.UpdatePlan("a",["first"]);
        var first=await synth.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        queue.UpdatePlan("b",["second"],1);
        queue.ClearPlan("a");
        first.Result.SetResult(File("first"));
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second=await synth.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("second",second.Text);
        string path=File("second");
        second.Result.SetResult(path);
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(played);
        Assert.True(queue.TryAnnounce("second"));
        Assert.Equal(path,Assert.Single(played));
    }
    [Fact]
    public async Task IndependentChannelsShareOnePreparedText()
    {
        var synth=new Synth();
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue=new SpeechAnnouncementQueue(synth,_=>{},
            m=>{if(m.StartsWith("TTS 文件缓存已就绪"))ready.TrySetResult();});
        queue.UpdatePlan("a",["shared"]);
        var request=await synth.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        queue.UpdatePlan("b",["shared"],1);
        request.Result.SetResult(File("shared"));
        await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.IsReady("shared"));
        Assert.False(synth.Requests.Reader.TryRead(out _));
    }
    [Fact]
    public async Task AdapterDoesNotDisposeAnExternallyOwnedQueue()
    {
        var synth=new Synth();
        await using var queue=new SpeechAnnouncementQueue(synth,_=>{});
        var adapter=new PlannedSpeechAnnouncer(queue,new Ets2LaWarningAnnouncer(play:()=>{}));
        await adapter.DisposeAsync();
        Assert.False(queue.IsStopped);
        Assert.False(synth.Disposed);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
