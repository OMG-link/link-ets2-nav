using System.Threading.Channels;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidancePlaybackManagerTests
{
    [Fact]
    public void SynthesisFallbackUsesNeutralToneInsteadOfFixedCustomRecording()
    {
        string directory=Path.Combine(Path.GetTempPath(),"guidance-fallback-"+Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(directory,"Settings"));
        try
        {
            File.WriteAllText(Path.Combine(directory,"fixed-500m.wav"),"audio");
            File.WriteAllText(Path.Combine(directory,"Settings","LinkEts2Nav.Audio.json"),
                "{\"filePath\":\"fixed-500m.wav\"}");
            Assert.Equal(Path.Combine(directory,"fixed-500m.wav"),Ets2LaGuidanceAudioPlayer.FallbackFile(directory));
            Assert.Equal(Path.Combine(directory,"Assets","Sounds","pre_alert.wav"),
                Ets2LaGuidanceAudioPlayer.FallbackFile(directory,allowCustom:false));
        }
        finally{Directory.Delete(directory,true);}
    }
    private sealed class Player:IGuidanceAudioPlayer
    {
        public Channel<(string File,TaskCompletionSource Done)> Started=Channel.CreateUnbounded<(string,TaskCompletionSource)>();
        public async Task PlayAsync(string file,CancellationToken token)
        {
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Writer.TryWrite((file,done));await done.Task.WaitAsync(token);
        }
    }
    private sealed class Synth:ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text,CancellationToken token)=>throw new InvalidOperationException("Cached test speech only.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    [Fact]
    public async Task QueuedTextCanRefreshWithoutReplayingAndWithdrawnEventsCanBeReplanned()
    {
        string directory=Path.Combine(Path.GetTempPath(),"guidance-refresh-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try
        {
            var files=new Dictionary<string,string>();foreach(var word in new[]{"50","80"})
            {string file=Path.Combine(directory,word+".wav");File.WriteAllText(file,word);files[word]=file;}
            await using var cache=new SpeechAnnouncementQueue(new Synth(),_=>{},preGenerated:files);
            var player=new Player();await using var manager=new GuidancePlaybackManager(cache,player,()=>null);
            manager.SetActive(false);manager.Enqueue(new("route","event","500",-500,10,"50"));
            manager.Refresh("route",[new("500","event",LinkEts2Nav.Core.Guidance.SpeechTriggerKind.Advance,0,"80")]);
            Assert.Equal("80",Assert.Single(manager.Pending).Text);
            manager.Withdraw("route",["event"]);Assert.Empty(manager.Pending);
            manager.Enqueue(new("route","event","500",-500,10,"80"));Assert.Single(manager.Pending);
            manager.SetActive(true);
            var next=await player.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
            Assert.Equal("80",Path.GetFileNameWithoutExtension(next.File));next.Done.SetResult();
        }
        finally{Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task InvalidFallbackKeepsPendingSpeechAndTheWorkerCanResume()
    {
        var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool repaired=false;
        var player=new Player();
        await using var manager=new GuidancePlaybackManager(null,player,()=>
        {
            if(!repaired){attempted.TrySetResult();throw new InvalidDataException("Broken fallback configuration.");}
            return "repaired.wav";
        });
        manager.Enqueue(new("route","turn","execution",0,0,"turn"));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.Single(manager.Pending);
        repaired=true;manager.SetActive(true);
        var next=await player.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.Equal("repaired.wav",next.File);next.Done.SetResult();
    }

    [Fact]
    public async Task PlaybackWaitsForCompletionAndOnlyNewerStagesOfSameEventReplacePendingSpeech()
    {
        string directory=Path.Combine(Path.GetTempPath(),"guidance-play-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try
        {
            var files=new Dictionary<string,string>();foreach(var word in new[]{"playing","500","200","execute","other"})
            {string file=Path.Combine(directory,word+".wav");File.WriteAllText(file,word);files[word]=file;}
            await using var cache=new SpeechAnnouncementQueue(new Synth(),_=>throw new InvalidOperationException("Preparation cannot play."),preGenerated:files);
            var player=new Player();await using var manager=new GuidancePlaybackManager(cache,player,()=>null);
            manager.Enqueue(new("route","first","first",0,10,"playing"));
            var first=await player.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
            manager.Enqueue(new("route","landmark","500",-500,10,"500"));
            manager.Enqueue(new("route","landmark","200",-200,10,"200"));
            manager.Enqueue(new("route","turn","execute",0,0,"execute"));
            manager.Enqueue(new("route","other","other",0,10,"other"));
            Assert.False(player.Started.Reader.TryRead(out _));Assert.Equal(3,manager.Pending.Count);
            first.Done.SetResult();
            foreach(string expected in new[]{"execute","200","other"})
            {
                var next=await player.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
                Assert.Equal(expected,Path.GetFileNameWithoutExtension(next.File));next.Done.SetResult();
            }
        }
        finally{Directory.Delete(directory,true);}
    }
}
