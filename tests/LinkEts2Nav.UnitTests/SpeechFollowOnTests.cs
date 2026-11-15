using System.Threading.Channels;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechFollowOnTests
{
    [Fact]
    public void SharedRequestFactoryKeepsProximityUsageIndependentOfCameraUid()
    {
        var sequence=new SpeechSequence("camera",new RouteSpeechEnd(100),
            [new("camera",0,"uncalibrated-proximity:ABC")],
            [new("camera:warning","camera",SpeechTriggerKind.Advance,0,"warning")]);
        var result=Assert.Single(Due(sequence));
        Assert.Equal("uncalibrated-proximity",QueuedGuidanceSpeech.From(result).EventDescription);
    }

    [Fact]
    public void NearbyPassageIsAppendedWithoutChangingEitherPhysicalSeries()
    {
        var now=DateTimeOffset.UtcNow;
        NavigationManeuver[] actions=[
            new("first",ManeuverKind.KeepRight,100,"a","b") {JunctionId="a",EndDistanceMeters=143.925},
            new("straight",ManeuverKind.Straight,182.164,"b","c")
                {JunctionId="b",EndDistanceMeters=232.665,StraightExitSides=NavigationSide.Right},
            new("last",ManeuverKind.KeepRight,495.277,"c","d") {JunctionId="c",EndDistanceMeters=538.628}];
        var scheduler=new SpeechTriggerScheduler();
        var plan=new NavigationEventPlanner().Build("s","r",now,0,actions,true,false,false,scheduler);
        var first=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("first@")&&t.Kind==SpeechTriggerKind.Execution);
        Assert.Equal("向右前方行驶，然后八十米直行通过当前路口。",first.Text);
        var follow=Assert.IsType<SpeechFollowOn>(first.FollowOn);
        var passage=Assert.Single(plan.Sequences,s=>s.Id==follow.SequenceId);
        Assert.Equal(232.665,passage.EndAlongMeters);
        Assert.Equal(143.925,Assert.Single(plan.Sequences,s=>s.Events.Any(e=>e.Id==first.EventId)).EndAlongMeters);
        Assert.Equal(follow.TriggerId,Assert.Single(passage.Triggers).Id);
        Assert.False(scheduler.IsConsumed("navigation",follow.TriggerId));
        var timeline=new GuidanceTimeline();timeline.Publish(plan);
        Assert.Contains("向右前方行驶。",scheduler.Prepare(timeline,now));
        Assert.Contains(first.Text,scheduler.Prepare(timeline,now));
    }

    private static SpeechSequence Sequence(string id,string text,double at,double end,SpeechFollowOn? follow=null)
        =>new(id,new RouteSpeechEnd(end),[new(id,at,text)],
            [new(id+":execute",id,SpeechTriggerKind.Execution,at,text){Priority=id=="first"?0:10,FollowOn=follow}]);
    private static IReadOnlyList<GuidanceTriggerResult> Due(params SpeechSequence[] sequences)
    {
        var now=DateTimeOffset.UtcNow;var timeline=new GuidanceTimeline();
        timeline.Publish(new("route","s","r",0,true,now,sequences));
        return new SpeechTriggerScheduler().Update(timeline,now,0);
    }
    private static IReadOnlyList<GuidanceTriggerResult> End(string id)
        =>Due(new SpeechSequence(id,new RouteSpeechEnd(-1),[new(id,-1,"")],[],ended:true));
    private static readonly SpeechFollowOn Follow=new("helper","helper","helper:execute","turn");
    private static SpeechSequence First()=>Sequence("first","turn then straight",0,100,Follow);
    private static SpeechSequence Helper()=>Sequence("helper","straight",0,200);

    [Fact]
    public async Task SuccessfulBodyAtomicallyRemovesTheAlreadyQueuedHelperAndRejectsLaterDuplicates()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(Due(First()),true);
        var first=await harness.Next();Assert.Equal("turn then straight",first.Text);
        harness.Manager.ApplyBatch(Due(Helper()),true);
        first.Done.SetResult();await harness.Played("turn then straight");
        Assert.Empty(harness.Manager.Pending);
        harness.Manager.ApplyBatch(Due(Helper()),true);Assert.Empty(harness.Manager.Pending);
        Assert.False(harness.Player.Started.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ExpiringUnplayedFirstSeriesLeavesItsHelperIntact()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(Due(First()),false);
        harness.Manager.ApplyBatch(End("first"),false);harness.Manager.ApplyBatch(Due(Helper()),true);
        var helper=await harness.Next();Assert.Equal("straight",helper.Text);helper.Done.SetResult();
        await harness.Played("straight");
        harness.Manager.ApplyBatch(Due(First()),true);Assert.Empty(harness.Manager.Pending);
    }

    [Fact]
    public async Task HelperPlayedFirstRemovesOnlyTheOptionalClause()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(Due(Helper()),true);
        var helper=await harness.Next();harness.Manager.ApplyBatch(Due(First()),true);
        helper.Done.SetResult();await harness.Played("straight");
        var first=await harness.Next();Assert.Equal("turn",first.Text);first.Done.SetResult();await harness.Played("turn");
    }

    [Fact]
    public async Task ExpiredHelperIsNotReintroducedByTheCompositeOrByRequeueing()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(End("helper"),false);
        harness.Manager.ApplyBatch(Due(First(),Helper()),true);
        var first=await harness.Next();Assert.Equal("turn",first.Text);first.Done.SetResult();await harness.Played("turn");
        Assert.Empty(harness.Manager.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPlaybackAndFallbackToneDoNotConfirmTheHelper(bool fallback)
    {
        await using var harness=new Harness(fallback);harness.Manager.ApplyBatch(Due(First()),true);
        var first=await harness.Next();harness.Manager.ApplyBatch(Due(Helper()),true);
        if(fallback)first.Done.SetResult();else first.Done.SetException(new IOException("device unavailable"));
        // Expiry prevents the failed primary from retrying ahead of the independent helper.
        harness.Manager.ApplyBatch(End("first"),true);
        var helper=await harness.Next();Assert.Equal(fallback?"beep":"straight",helper.Text);helper.Done.SetResult();
    }

    [Fact]
    public async Task CancelledPlaybackDoesNotConfirmTheHelper()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(Due(First()),true);
        var first=await harness.Next();harness.Manager.ApplyBatch(Due(Helper()),true);
        first.Done.SetCanceled(TestContext.Current.CancellationToken);harness.Manager.ApplyBatch(End("first"),true);
        var helper=await harness.Next();Assert.Equal("straight",helper.Text);helper.Done.SetResult();
    }

    [Fact]
    public async Task RemovingTheHelperFromTheRouteRemovesTheOptionalClause()
    {
        await using var harness=new Harness();harness.Manager.ApplyBatch(Due(First()),false);
        harness.Manager.Withdraw("route",["helper"]);harness.Manager.SetActive(true);
        var first=await harness.Next();Assert.Equal("turn",first.Text);first.Done.SetResult();
    }

    private sealed class Player:IGuidanceAudioPlayer
    {
        public readonly Channel<(string Text,TaskCompletionSource Done)> Started=Channel.CreateUnbounded<(string,TaskCompletionSource)>();
        public async Task PlayAsync(string file,CancellationToken token)
        {
            var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Started.Writer.TryWrite((Path.GetFileNameWithoutExtension(file),done));await done.Task.WaitAsync(token);
        }
    }
    private sealed class Synth:ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text,CancellationToken token)=>Task.FromException<string>(new IOException("test synthesis failure"));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Harness:IAsyncDisposable
    {
        private readonly string directory=Path.Combine(Path.GetTempPath(),"follow-on-"+Guid.NewGuid());
        private readonly SpeechAnnouncementQueue cache;
        private readonly Channel<string> completed=Channel.CreateUnbounded<string>();
        public readonly Player Player=new();
        public readonly GuidancePlaybackManager Manager;
        public Harness(bool fallback=false)
        {
            Directory.CreateDirectory(directory);var files=new Dictionary<string,string>();
            foreach(string text in new[]{"turn","straight","turn then straight"})
            {string file=Path.Combine(directory,text+".wav");File.WriteAllText(file,"test");if(!fallback)files[text]=file;}
            cache=new(new Synth(),_=>{},preGenerated:files);
            Manager=new(cache,Player,()=>fallback?"beep.wav":null);
            Manager.Played+=speech=>completed.Writer.TryWrite(speech.Text);
        }
        public async Task<(string Text,TaskCompletionSource Done)> Next()
            =>await Player.Started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task Played(string text)
            =>Assert.Equal(text,await completed.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        public async ValueTask DisposeAsync()
        {await Manager.DisposeAsync();await cache.DisposeAsync();Directory.Delete(directory,true);}
    }
}
