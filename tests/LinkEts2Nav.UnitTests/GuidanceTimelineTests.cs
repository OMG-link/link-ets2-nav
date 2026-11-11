using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidanceTimelineTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static GuidanceSourcePlan Plan(double player,string text="500",string revision="a")=>new("route","session",revision,player,true,Now,
        [new("landmark",1000,"landmark")],[new("500","landmark",SpeechTriggerKind.Advance,500,text){SequenceOrder=-500},
        new("200","landmark",SpeechTriggerKind.Advance,800,"200"){SequenceOrder=-200,Supersedes=["500"]}]);
    [Fact]
    public void AppendAndRouteRevisionKeepConsumedPrefixButReplaceFutureContent()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(Plan(500));Assert.Single(scheduler.Update(timeline,Now,0));
        var next=Plan(500,"changed","b");
        timeline.Publish(next with{Events=[..next.Events,new("later",2000,"later")],Triggers=[..next.Triggers,new("later-500","later",SpeechTriggerKind.Advance,1500,"later")]});
        Assert.Empty(scheduler.Update(timeline,Now,0));
        Assert.Equal(new[]{"200","later"},scheduler.Prepare(timeline,Now));
        timeline.Publish(Plan(800,revision:"c"));Assert.Equal("200",Assert.Single(scheduler.Update(timeline,Now,0)).Trigger.Text);
    }
    [Fact]
    public void NewerStageSuppressesAnOlderStageCrossedInTheSameSample()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();timeline.Publish(Plan(850));
        Assert.Equal("200",Assert.Single(scheduler.Update(timeline,Now,0)).Trigger.Id);
        Assert.Equal(TriggerDisposition.Superseded,scheduler.Disposition("route","500"));
    }
    [Theory]
    [InlineData(59.9,TriggerDisposition.Queued)] [InlineData(60,TriggerDisposition.SpeedSkipped)]
    public void SpeedIsCheckedOnlyOnTriggerAndDoesNotRemovePreparation(double speed,TriggerDisposition expected)
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(new("route","s","a",0,true,Now,[new("turn",100,"turn")],
            [new("100","turn",SpeechTriggerKind.Advance,0,"100"){SkipAtSpeedKph=60}]));
        Assert.Equal(new[]{"100"},scheduler.Prepare(timeline,Now));
        Assert.Equal(expected,Assert.Single(scheduler.Update(timeline,Now,speed/3.6)).Disposition);
        Assert.Empty(scheduler.Update(timeline,Now,0));
    }
    [Fact]
    public void PausedAndStalePlansCannotTriggerOrBecomePlaybackPreparation()
    {
        var t=new GuidanceTimeline();var s=new SpeechTriggerScheduler();t.Publish(Plan(900) with{Active=false});
        Assert.Empty(s.Update(t,Now,0));Assert.Empty(s.Prepare(t,Now));
        t.Publish(Plan(900));Assert.Empty(s.Update(t,Now.AddSeconds(3),0));
        Assert.False(s.IsConsumed("route","200"));
    }
    [Fact]
    public void TriggerLocationsAreExplicitlyBeforeTheEventAndDisabledPointsCanStillPrepareAudio()
    {
        var p=Plan(0);var t=new GuidanceTimeline();var s=new SpeechTriggerScheduler();
        t.Publish(p with{Triggers=p.Triggers.Select(x=>x with{Enabled=false}).ToArray(),PlayerAlongMeters=900});
        Assert.All(t.Sources[0].Triggers,x=>Assert.True(x.AlongMeters<t.Sources[0].Events[0].AlongMeters));
        Assert.Equal(2,s.Prepare(t,Now).Count);Assert.Empty(s.Update(t,Now,0));
    }
}
