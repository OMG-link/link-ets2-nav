using System.Text.Json;
using System.Threading.Channels;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechUsageTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"nav-speech-usage-"+Guid.NewGuid().ToString("N"));
    private static CancellationToken Token=>TestContext.Current.CancellationToken;
    private static SpeechUsageEntry Row(SpeechUsageCollector usage,string text)=>Assert.Single(usage.Capture().Entries,e=>e.Text==text);
    private static QueuedGuidanceSpeech Item(string text)=>new("navigation","event","trigger",1,10,text){Stage="Advance",EventDescription="左转"};
    private string Audio(string name){Directory.CreateDirectory(directory);string path=Path.Combine(directory,name+".wav");File.WriteAllText(path,"test");return path;}
    [Fact]
    public async Task RepeatedNotificationRefreshCountsOneEnqueueAndOnlyCompletionCountsPlayed()
    {
        var usage=new SpeechUsageCollector();var player=new Player();
        await using var cache=new SpeechAnnouncementQueue(new ThrowSynth(),_=>{},preGenerated:new Dictionary<string,string>{{"准备出发",Audio("start")}},usage:usage);
        await using var manager=new GuidancePlaybackManager(cache,player,()=>null,usage:usage);
        manager.SetActive(false);
        for(int i=0;i<30;i++)manager.Notify("journey",new("start","准备出发"));
        Assert.Equal(1,Row(usage,"准备出发").EnqueuedCount);Assert.Equal(0,Row(usage,"准备出发").PlayedCount);
        manager.SetActive(true);var playing=await player.Started.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),Token);
        Assert.Equal(0,Row(usage,"准备出发").PlayedCount);playing.Done.SetResult();
        await Until(()=>Row(usage,"准备出发").PlayedCount==1);
        Assert.Equal(1,Row(usage,"准备出发").ExactPackPlayedCount);Assert.Equal(0,Row(usage,"准备出发").PreparationCount);
    }
    [Fact]
    public async Task SupersededAndWithdrawnSpeechDoesNotCountAsPlayed()
    {
        var usage=new SpeechUsageCollector();var player=new Player();
        await using var cache=new SpeechAnnouncementQueue(new ThrowSynth(),_=>{},preGenerated:new Dictionary<string,string>{{"500",Audio("far")},{"200",Audio("near")}},usage:usage);
        await using var manager=new GuidancePlaybackManager(cache,player,()=>null,usage:usage);
        manager.SetActive(false);
        for(int i=0;i<10;i++)manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","camera","500",-500,10,"500")),false);
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","camera","200",-200,10,"200")),false);
        Assert.Equal(1,Row(usage,"500").EnqueuedCount);Assert.Equal(1,Row(usage,"500").SupersededCount);
        manager.Withdraw("navigation",["camera"]);Assert.Equal(1,Row(usage,"200").WithdrawnCount);
        Assert.All(usage.Capture().Entries,e=>Assert.Equal(0,e.PlayedCount));
    }
    [Fact]
    public async Task PlaybackFailureRetryDoesNotCreateAnotherEnqueueAndBeepIsNotSpeech()
    {
        var usage=new SpeechUsageCollector();var player=new Player{FailFirst=true};
        await using var manager=new GuidancePlaybackManager(null,player,()=>"fallback.wav",usage:usage);
        manager.Notify("signal",new("green","红灯即将变绿。"));
        var retry=await player.Started.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3),Token);
        var row=Row(usage,"红灯即将变绿。");Assert.Equal(1,row.EnqueuedCount);Assert.Equal(1,row.PlaybackFailureCount);Assert.Equal(0,row.PlayedCount);
        retry.Done.SetResult();await Until(()=>Row(usage,row.Text).FallbackCount==1);
        Assert.Equal(0,Row(usage,row.Text).PlayedCount);Assert.Equal(1,Row(usage,row.Text).EnqueuedCount);
    }
    [Fact]
    public async Task PreparationReportsActualWorkerCacheMetadataAndElapsedTimeWithoutCountingPlans()
    {
        var usage=new SpeechUsageCollector();var synth=new SuccessfulSynth(Audio("generated"));
        await using var queue=new SpeechAnnouncementQueue(synth,_=>{},usage:usage);
        for(int i=0;i<30;i++)queue.UpdatePlan("navigation",["两百米后，左转。"]);
        await Until(()=>usage.Capture().Entries.Count>0);
        var row=Row(usage,"两百米后，左转。");Assert.Equal(1,row.PreparationCount);Assert.Equal(1,row.WorkerCacheHitCount);
        Assert.True(row.PreparationMilliseconds>=0);Assert.Equal(0,row.EnqueuedCount);Assert.Equal(0,row.PlayedCount);
        usage.Enqueued(Item("二百米后，左转。"));Assert.Equal(2,usage.Capture().Entries.Count);
    }
    [Fact]
    public async Task SupersededInFlightPreparationIsCostedButNotAcceptedAsReadyAudio()
    {
        var usage=new SpeechUsageCollector();var synth=new WaitingSynth();
        await using var queue=new SpeechAnnouncementQueue(synth,_=>{},usage:usage);
        queue.UpdatePlan("navigation",["左转。"]);await synth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3),Token);
        string current=Audio("current"),old=Audio("old");
        queue.ReplacePreparedAudio(new Dictionary<string,string>{{"左转。",current}},()=>{});
        synth.Done.SetResult(old);await Until(()=>usage.Capture().Entries.Count>0);
        Assert.True(queue.TryGetAudio("左转。",out string file,out bool exact));Assert.Equal(current,file);Assert.True(exact);
        var row=Row(usage,"左转。");Assert.Equal(1,row.PreparationCount);Assert.Equal(1,row.PreparationCancelledCount);
        Assert.Equal(0,row.PreparationFailureCount);Assert.Equal(1,row.WorkerCacheUnknownCount);Assert.Equal(0,row.PlayedCount);
    }
    [Fact]
    public async Task CheckpointsReplaceTotalsAndRestartNeverReAddsThePreviousSnapshot()
    {
        await using(var first=new SpeechUsageStore(directory))
        {
            first.Collector.Enqueued(Item("左转。"));first.Collector.Played("左转。",SpeechPlaybackOrigin.Prepared);
            await first.FlushAsync();await first.FlushAsync();
        }
        await using(var second=new SpeechUsageStore(directory))
        {
            Assert.Equal(1,Row(second.Collector,"左转。").PlayedCount);Assert.Equal(2,second.Collector.Capture().CollectionSessions);
            second.Collector.Played("左转。",SpeechPlaybackOrigin.Prepared);await second.FlushAsync();
            string export1=await second.ExportAsync(),export2=await second.ExportAsync();Assert.NotEqual(export1,export2);
            using var exported=JsonDocument.Parse(File.ReadAllText(Path.Combine(export1,"speech-usage.json")));
            Assert.Equal(2,exported.RootElement.GetProperty("Entries")[0].GetProperty("PlayedCount").GetInt64());
            Assert.Contains("左转。",File.ReadAllText(Path.Combine(export1,"speech-usage.csv")));
        }
        await using var third=new SpeechUsageStore(directory);Assert.Equal(2,Row(third.Collector,"左转。").PlayedCount);
    }
    [Fact]
    public async Task CorruptCheckpointFallsBackToBackupAndDiskFailureDoesNotDiscardMemoryCounts()
    {
        await using(var first=new SpeechUsageStore(directory))
        {first.Collector.Enqueued(Item("左转。"));await first.FlushAsync();first.Collector.Played("左转。",SpeechPlaybackOrigin.Prepared);await first.FlushAsync();}
        await File.WriteAllTextAsync(Path.Combine(directory,"summary.json"),"invalid",Token);
        await using(var recovered=new SpeechUsageStore(directory))
        {Assert.Equal(1,Row(recovered.Collector,"左转。").EnqueuedCount);Assert.NotNull(recovered.LastError);}
        Assert.Single(Directory.GetFiles(directory,"summary.unreadable-*.json"));
        string blocked=Path.Combine(directory,"blocked");await File.WriteAllTextAsync(blocked,"not a directory",Token);
        await using var failure=new SpeechUsageStore(blocked);failure.Collector.Played("右转。",SpeechPlaybackOrigin.Prepared);
        await failure.FlushAsync();Assert.NotNull(failure.LastError);Assert.Equal(1,Row(failure.Collector,"右转。").PlayedCount);
    }
    private static async Task Until(Func<bool> done)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(!done())await Task.Delay(10,timeout.Token);
    }
    private sealed class ThrowSynth:ISpeechSynthesizer
    {public Task<string>SynthesizeAsync(string text,CancellationToken token)=>throw new InvalidOperationException("Preloaded only.");public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    private sealed class SuccessfulSynth(string path):ISpeechSynthesizer,ISpeechSynthesisDiagnostics
    {public bool? LastCacheHit=>true;public Task<string>SynthesizeAsync(string text,CancellationToken token)=>Task.FromResult(path);public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    private sealed class WaitingSynth:ISpeechSynthesizer
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string>SynthesizeAsync(string text,CancellationToken token){Started.TrySetResult();return Done.Task.WaitAsync(token);}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Player:IGuidanceAudioPlayer
    {
        public bool FailFirst;public Channel<(string File,TaskCompletionSource Done)> Started=Channel.CreateUnbounded<(string,TaskCompletionSource)>();
        public async Task PlayAsync(string file,CancellationToken token)
        {if(FailFirst){FailFirst=false;throw new IOException("test failure");}var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Started.Writer.TryWrite((file,done));await done.Task.WaitAsync(token);}
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
