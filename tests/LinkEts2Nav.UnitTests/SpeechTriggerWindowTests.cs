using System.Text.Json;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class SpeechTriggerWindowTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-14T00:00:00Z");
    private static GuidanceSourcePlan Plan(double position,Func<SpeechTriggerContext,bool> callback,bool strict=false)=>new("nav","s","r",position,true,Now,
        [new SpeechSequence("turn",new RouteSpeechEnd(130),[new("turn",100,"turn")],
            [new("final","turn",SpeechTriggerKind.Execution,80,"left"){Window=new(0,callback,strict),Priority=0}])]);
    [Fact]
    public void PollsOnlyInsideTheActiveWindowAndStopsAfterFirstSuccess()
    {
        int calls=0;var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        bool Poll(SpeechTriggerContext c){calls++;Assert.Equal(100-c.PlayerAlongMeters,c.DistanceToEventMeters);return c.SpeedMetersPerSecond>=20;}
        timeline.Publish(Plan(-1,Poll));Assert.Empty(scheduler.Update(timeline,Now,20));Assert.Equal(0,calls);
        timeline.Publish(Plan(10,Poll));Assert.Empty(scheduler.Update(timeline,Now,10));Assert.Equal(1,calls);
        timeline.Publish(Plan(20,Poll));Assert.Single(scheduler.Update(timeline,Now,20));Assert.Equal(2,calls);
        timeline.Publish(Plan(30,Poll));Assert.Empty(scheduler.Update(timeline,Now,20));Assert.Equal(2,calls);
    }
    [Theory] [InlineData(80)] [InlineData(90)]
    public void ReachingOrJumpingOverTheDeadlineForcesTheCueWithoutCallingThePredicate(double position)
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(Plan(position,_=>throw new Exception("Deadline must not poll")));
        Assert.Equal(TriggerDisposition.Queued,Assert.Single(scheduler.Update(timeline,Now,0)).Disposition);
    }
    [Fact]
    public void PreparationNeverPollsAndDisabledOrStalePlansDoNotPoll()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();int calls=0;
        var plan=Plan(10,_=>{calls++;return true;});timeline.Publish(plan);
        Assert.Equal(new[]{"left"},scheduler.Prepare(timeline,Now));Assert.Equal(0,calls);
        timeline.Publish(plan with{Active=false});Assert.Empty(scheduler.Update(timeline,Now,0));
        timeline.Publish(plan with{Sequences=plan.Sequences.Select(s=>s.MapTriggers(t=>t with{Enabled=false})).ToArray()});
        Assert.Empty(scheduler.Update(timeline,Now,0));timeline.Publish(plan);
        Assert.Empty(scheduler.Update(timeline,Now.AddSeconds(3),0));Assert.Equal(0,calls);
    }
    [Fact]
    public void StrictStartWaitsUntilAfterThePreviousActionBoundary()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();int calls=0;
        timeline.Publish(Plan(0,_=>{calls++;return true;},true));Assert.Empty(scheduler.Update(timeline,Now,30));
        timeline.Publish(Plan(.1,_=>{calls++;return true;},true));Assert.Single(scheduler.Update(timeline,Now,30));Assert.Equal(1,calls);
    }
    [Fact]
    public void SeriesEndStillClearsAWindowSkippedByOneSample()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();timeline.Publish(Plan(150,_=>false));
        var batch=scheduler.Update(timeline,Now,0);
        Assert.Contains(batch,r=>r.Disposition==TriggerDisposition.Ended);
        Assert.DoesNotContain(batch,r=>r.Disposition==TriggerDisposition.Queued);
    }
    [Fact]
    public void AWindowDoesNotBlockOtherSeriesAndRetiresItsOwnEarlierPendingPoints()
    {
        var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=Plan(10,_=>true);var seq=plan.Sequences[0];
        plan=plan with{Sequences=[new SpeechSequence("turn",seq.End,seq.Events,
            [new("earlier","turn",SpeechTriggerKind.Advance,20,"earlier"){SequenceOrder=-100},..seq.Triggers]),
            new SpeechSequence("camera",new RouteSpeechEnd(200),[new("camera",150,"camera")],
                [new("camera-first","camera",SpeechTriggerKind.Advance,10,"camera")])]};
        timeline.Publish(plan);var batch=scheduler.Update(timeline,Now,20);
        Assert.Equal(2,batch.Count(r=>r.Disposition==TriggerDisposition.Queued));
        Assert.Equal(TriggerDisposition.Superseded,scheduler.Disposition("nav","earlier"));
        timeline.Publish(plan with{PlayerAlongMeters=30});Assert.Empty(scheduler.Update(timeline,Now,20));
    }
    [Fact]
    public void WindowsValidateTheirBoundsAndCanBeRecordedWithoutSerializingCallbacks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(()=>new SpeechTriggerWindow(double.NaN,_=>true));
        Assert.Throws<ArgumentNullException>(()=>new SpeechTriggerWindow(0,null!));
        var plan=Plan(0,_=>true);
        Assert.Throws<ArgumentException>(()=>plan.Sequences[0].MapTriggers(t=>t with{Window=new(81,_=>true)}));
        Assert.DoesNotContain("ShouldTrigger",JsonSerializer.Serialize(plan));
    }
}
