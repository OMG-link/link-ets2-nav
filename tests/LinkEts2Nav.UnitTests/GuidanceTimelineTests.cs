using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class GuidanceTimelineTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void ADisappearingEventRetainsItsEndUntilTheCrossingUpdate(double end)
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(new("e","s","r",0,true,Now,[new("gone",100,"camera")],
            [new("first","gone",SpeechTriggerKind.Advance,0,"first")])
            {CoordinateFrame="f",Sequences=[new("gone",end)]});
        scheduler.Update(timeline,Now,0);
        int ended=0;
        foreach(double player in new[]{110d,160d})
        {
            timeline.Publish(new("e","s","r",player,true,Now,[],[]){CoordinateFrame="f"});
            var results=scheduler.Update(timeline,Now,0);
            ended+=results.Count(r=>r.Disposition==TriggerDisposition.Ended);
            if(player<=end)Assert.DoesNotContain(results,r=>r.Disposition==TriggerDisposition.Ended);
        }
        Assert.Equal(1,ended);
        timeline.Publish(new("e","s","r",170,true,Now,[],[]){CoordinateFrame="f"});
        Assert.Empty(timeline.Sources[0].Events);
    }
    [Fact]
    public void SequenceEndDiagnosticsContainOnlyJsonNumbers()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        var now=DateTimeOffset.UtcNow;
        timeline.Publish(new("navigation","s","r",11,true,now,[new("event",5,"turn")],[])
            {Sequences=[new("event",10)]});
        var ended=Assert.Single(scheduler.Update(timeline,now,0));
        Assert.Equal(TriggerDisposition.Ended,ended.Disposition);
        Assert.Contains("sequence-end:",System.Text.Json.JsonSerializer.Serialize(ended));
    }
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static GuidanceSourcePlan Plan(double player,string text="500",string revision="a")=>new("route","session",revision,player,true,Now,
        [new("landmark",1000,"landmark")],[new("500","landmark",SpeechTriggerKind.Advance,500,text){SequenceOrder=-500},
        new("200","landmark",SpeechTriggerKind.Advance,800,"200"){SequenceOrder=-200,Supersedes=["500"]}]);
    [Fact]
    public void PrefixTrimmingDoesNotLoseAnExecutionPointWhoseEventJustLeftTheDetector()
    {
        var progress=new RouteProgressTracker();
        DirectedRoad Road(string id,ulong from,ulong to,float start)=>new(id,from,to,[new(start,0,0),new(start+100,0,0)]);
        var a=Road("a",1,2,0);var b=Road("b",2,3,100);var c=Road("c",3,4,200);
        double first=progress.Update("s",[a,b],70);string frame=progress.Frame;
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(new("navigation","s","old",first,true,Now,[new("turn",100,"turn")],
            [new("execute","turn",SpeechTriggerKind.Execution,80,"左转。"){Priority=0}]){CoordinateFrame=frame});
        Assert.Empty(scheduler.Update(timeline,Now,0));
        double second=progress.Update("s",[b,c],10);Assert.Equal(110,second);Assert.Equal(frame,progress.Frame);
        timeline.Publish(new("navigation","s","trimmed",second,true,Now,[new("later",220,"later")],[]){CoordinateFrame=frame});
        Assert.Equal("左转。",Assert.Single(scheduler.Update(timeline,Now,0)).Trigger.Text);
        progress.Update("s",[Road("unrelated",10,11,300)],0);Assert.NotEqual(frame,progress.Frame);
    }

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
    public void WithdrawingAnEventForgetsStagesNoLongerPresentInTheLatestPlan()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(Plan(500));scheduler.Update(timeline,Now,0);
        timeline.Publish(Plan(800));scheduler.Update(timeline,Now,0);
        scheduler.ForgetEvents("route",["landmark"]);
        Assert.False(scheduler.IsConsumed("route","500"));
        Assert.False(scheduler.IsConsumed("route","200"));
        timeline.Publish(Plan(500));
        Assert.Equal("500",Assert.Single(scheduler.Update(timeline,Now,0)).Trigger.Id);
    }
    [Fact]
    public void NewerStageSuppressesAnOlderStageCrossedInTheSameSample()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();timeline.Publish(Plan(850));
        Assert.Equal("200",Assert.Single(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Id);
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
