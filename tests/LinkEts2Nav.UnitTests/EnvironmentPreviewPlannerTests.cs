using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class EnvironmentPreviewPlannerTests
{
    [Fact]
    public void ANewEarlierLandmarkReplansOnlyTheUnconsumedFirstPoint()
    {
        var p=new EnvironmentPreviewPlanner();var scheduler=new SpeechTriggerScheduler();
        p.Build("e",0,[E("far","camera",750,750)],scheduler,true);
        var inserted=p.Build("e",200,[E("near","camera",500,500),E("far","camera",750,750)],scheduler,true);
        var far=Assert.Single(inserted.Triggers,t=>t.EventId=="far");
        Assert.Equal(500,far.AlongMeters);Assert.True(far.StrictCrossing);Assert.Equal("250:far",far.Text);
    }
    [Fact]
    public void ANewPredecessorDoesNotReplaySpokenContentAndGatesTheRemainingStage()
    {
        var p=new EnvironmentPreviewPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var initial=p.Build("e",0,[E("far","camera",750,750)],scheduler,true);
        timeline.Publish(new("e","s","r",250,true,Now,initial.Sequences));
        scheduler.Update(timeline,Now,0);
        var inserted=p.Build("e",300,[E("near","camera",600,600),E("far","camera",750,750)],scheduler,true);
        var second=Assert.Single(inserted.Triggers,t=>t.Id=="far:200");
        Assert.Equal(600,second.AlongMeters);Assert.True(second.StrictCrossing);
        timeline.Publish(new("e","s","r",300,true,Now,inserted.Sequences));
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Event.Id=="far"&&r.Disposition==TriggerDisposition.Queued);
    }
    [Fact]
    public void ATemporarilyUnmappedPredecessorStillBlocksItsSharedSuccessor()
    {
        var p=new EnvironmentPreviewPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var initial=p.Build("e",0,[E("far","camera",1000,1000)],scheduler,true);
        timeline.Publish(new("e","s","r",500,true,Now,initial.Sequences));
        scheduler.Update(timeline,Now,0);
        p.Build("e",600,[E("near","camera",800,800),E("far","camera",1000,1000)],scheduler,true);
        p.Rebase([E("far","camera",500,500)]);
        var missing=p.Build("e",350,[E("far","camera",500,500)],scheduler,true);
        Assert.Empty(missing.Triggers); // The previous end has no coordinate in this frame.
        var passed=p.Build("e",350,[E("near","camera",350) with{Ended=true},E("far","camera",500,500)],scheduler,true);
        Assert.Contains(passed.Triggers,t=>t.Id=="far:200");
        timeline.Publish(new("e","s","r",350,true,Now,passed.Sequences));
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Trigger.Id=="far:200"&&r.Disposition==TriggerDisposition.Queued);
    }
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static EnvironmentPreviewEvent E(string id,string kind,double along,double? end=null)=>new(id,kind,along,end,d=>d+":"+id);
    [Theory]
    [InlineData(450,450,2)] [InlineData(350,350,2)] [InlineData(349.999,300,1)] [InlineData(200,200,1)] [InlineData(150,150,1)]
    [InlineData(49,0,1)] [InlineData(500,500,2)] [InlineData(700,500,2)]
    public void FirstDistanceDeterminesTheWholeEnvironmentProfile(double distance,int first,int count)
    {
        var p=new EnvironmentPreviewPlanner();var s=new SpeechTriggerScheduler();var plan=p.Build("e",0,[E("a","camera",distance,distance)],s,true);
        Assert.Equal(count,plan.Triggers.Count);Assert.Equal(first+":a",plan.Triggers[0].Text);
        Assert.Equal(Math.Max(0,distance-500),plan.Triggers[0].AlongMeters);
    }
    [Fact]
    public void OnlyTheSameKindDelaysAFirstPointAndItsDistanceIsFrozenBeforeArrival()
    {
        var p=new EnvironmentPreviewPlanner();var s=new SpeechTriggerScheduler();
        var plan=p.Build("e",0,[E("a","camera",300,300),E("b","camera",750,750),E("lane","lane",700,700)],s,true);
        var camera=Assert.Single(plan.Triggers,t=>t.Id=="b:first");Assert.Equal(300,camera.AlongMeters);Assert.Equal("450:b",camera.Text);
        Assert.Equal(new double[]{200,500},plan.Triggers.Where(t=>t.EventId=="lane").Select(t=>t.AlongMeters));
        var later=p.Build("e",310,[E("b","camera",750,750)],s,true);
        Assert.Equal(camera,Assert.Single(later.Triggers,t=>t.Id=="b:first"));
        Assert.Contains(later.Triggers,t=>t.Id=="b:200");
    }
    [Fact]
    public void UnknownEarlierEndWaitsForAPassageFactInsteadOfInventingABoundary()
    {
        var p=new EnvironmentPreviewPlanner();var s=new SpeechTriggerScheduler();
        var plan=p.Build("e",0,[E("a","camera",100),E("b","camera",450,450)],s,true);
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId=="b");
        var later=p.Build("e",100,[E("a","camera",100) with{Ended=true},E("b","camera",450,450)],s,true);
        Assert.Equal("350:b",Assert.Single(later.Triggers,t=>t.Id=="b:first").Text);
        Assert.Contains(later.Triggers,t=>t.Id=="b:200");
    }
    [Fact]
    public void APreviousEndAndNextFirstPointAtTheSameCoordinateAreBothConsumed()
    {
        var p=new EnvironmentPreviewPlanner();var s=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=p.Build("e",0,[E("a","camera",300,300),E("b","camera",750,750)],s,true);
        timeline.Publish(new("e","s","r",300,true,Now,plan.Sequences));
        Assert.DoesNotContain(s.Update(timeline,Now,0),r=>r.Event.Id=="b"&&r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(new("e","s","r",301,true,Now,plan.Sequences));
        var results=s.Update(timeline,Now,0);
        Assert.Contains(results,r=>r.Event.Id=="a"&&r.Disposition==TriggerDisposition.Ended);
        Assert.Contains(results,r=>r.Event.Id=="b"&&r.Disposition==TriggerDisposition.Queued&&r.Trigger.Text=="450:b");
    }
}
