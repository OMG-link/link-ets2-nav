using System.Threading.Channels;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidancePlaybackManagerTests
{
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
            var first=await player.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            manager.Enqueue(new("route","landmark","500",-500,10,"500"));
            manager.Enqueue(new("route","landmark","200",-200,10,"200"));
            manager.Enqueue(new("route","turn","execute",0,0,"execute"));
            manager.Enqueue(new("route","other","other",0,10,"other"));
            Assert.False(player.Started.Reader.TryRead(out _));Assert.Equal(3,manager.Pending.Count);
            first.Done.SetResult();
            foreach(string expected in new[]{"execute","200","other"})
            {
                var next=await player.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(expected,Path.GetFileNameWithoutExtension(next.File));next.Done.SetResult();
            }
        }
        finally{Directory.Delete(directory,true);}
    }
}
