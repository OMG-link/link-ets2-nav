using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class SpeechSequenceLifecycleTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static NavigationManeuver Turn(string id,double start,double? end)=>new(id,ManeuverKind.TurnLeft,start,"in","out")
        {JunctionId=id,EndDistanceMeters=end};
    private static GuidanceSourcePlan Plan(double player)=>new("navigation","s","r",player,true,Now,
        [GuidanceTestSeries.Route("event",150,[new("event",100,"turn")],
            [new("500","event",SpeechTriggerKind.Advance,-400,"500"){SequenceOrder=-500},
            new("execute","event",SpeechTriggerKind.Execution,80,"turn"){SequenceOrder=0,Priority=0}])])
        {CoordinateFrame="f"};

    [Fact]
    public void EndIsStrictlyAfterTheBoundaryAndConsumedPointsDoNotResurrect()
    {
        var t=new GuidanceTimeline();var s=new SpeechTriggerScheduler();
        t.Publish(Plan(0));Assert.Equal("500",Assert.Single(s.Update(t,Now,0)).Trigger.Text);
        t.Publish(Plan(150));Assert.Equal("turn",Assert.Single(s.Update(t,Now,0)).Trigger.Text);
        t.Publish(Plan(151));Assert.Equal(TriggerDisposition.Ended,Assert.Single(s.Update(t,Now,0)).Disposition);
        t.Publish(Plan(200));Assert.Empty(s.Update(t,Now,0));Assert.Empty(s.Prepare(t,Now));
    }

    [Fact]
    public void UnknownEndRemainsUnpublishedUntilThePhysicalBoundaryIsKnown()
    {
        var t=new GuidanceTimeline();var s=new SpeechTriggerScheduler();var p=new NavigationEventPlanner();
        t.Publish(p.Build("s","r",Now,0,[Turn("turn",100,null)],true,false,false,s));
        Assert.Empty(t.Sources[0].Sequences);Assert.Empty(s.Prepare(t,Now));Assert.Empty(s.Update(t,Now,0));
        t.Publish(p.Build("s","r",Now,10,[Turn("turn",90,140)],true,false,false,s));
        Assert.Single(t.Sources[0].Sequences);Assert.Single(s.Update(t,Now,0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextBriefingIsFrozenAtPreviousPhysicalEndAndEnvironmentDoesNotBlockIt(bool environment)
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        NavigationManeuver[] input=environment?
            [Turn("first",100,150),new("narrow",ManeuverKind.MergeLeft,350,"lane","lane")
                {EndDistanceMeters=350,AnnouncementMode=NavigationAnnouncementMode.DistancePreview},Turn("next",600,650)]:
            [Turn("first",100,150),Turn("next",600,650)];
        var first=p.Build("s","r",Now,0,input,true,false,false,s);
        var cue=Assert.Single(first.Triggers,x=>x.Id.StartsWith("next@")&&x.Kind==SpeechTriggerKind.Overview);
        Assert.Equal(150,cue.AlongMeters);Assert.Equal("四百五十米后，左转。",cue.Text);Assert.True(cue.Enabled);
        Assert.DoesNotContain(first.Triggers,x=>x.Id.StartsWith("next@")&&x.Id.EndsWith(":200"));
        t.Publish(first);s.Update(t,Now,0);
        var later=p.Build("s","r",Now,175,[Turn("next",425,475)],true,false,false,s);
        var stable=Assert.Single(later.Triggers,x=>x.Id==cue.Id);
        Assert.Equal(cue,stable);
        t.Publish(later);Assert.Contains(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued&&r.Trigger.Text==cue.Text);
    }

    [Fact]
    public void UnknownPreviousEndDoesNotUseItsEntryAsTheNextBriefingAnchor()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();
        var unknown=p.Build("s","r",Now,0,[Turn("first",100,null),Turn("next",600,650)],true,false,false,s);
        Assert.DoesNotContain(unknown.Triggers,t=>t.Id.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview);
        var known=p.Build("s","r",Now,10,[Turn("first",90,140),Turn("next",590,640)],true,false,false,s);
        Assert.Equal(150,Assert.Single(known.Triggers,t=>t.Id.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
    }

    [Fact]
    public void CoincidentRoundaboutExitsConsumeInOrderAndKeepTheLatestExit()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        NavigationManeuver Exit(string id,int n)=>new(id,n==3?ManeuverKind.ExitRoundabout:ManeuverKind.PassRoundaboutExit,160,"r","r")
            {JunctionId="ring",EndDistanceMeters=300,ExecutionOnly=true,SpeechSequenceId="ring-exits",SpeechSequenceOrder=n,
                Instruction=n==3?"驶出环岛":"经过第"+n+"出口"};
        NavigationManeuver[] input=[new("enter",ManeuverKind.EnterRoundabout,100,"r","r"){JunctionId="ring",EndDistanceMeters=300},Exit("one",1),Exit("two",2),Exit("out",3)];
        t.Publish(p.Build("s","r",Now,0,input,true,false,false,s));s.Update(t,Now,10);
        var prepared=s.Prepare(t,Now);Assert.Contains("驶出环岛。",prepared);
        t.Publish(p.Build("s","r",Now,140,[],true,false,false,s));
        var result=s.Update(t,Now,10);
        Assert.Equal("驶出环岛。",Assert.Single(result,r=>r.Disposition==TriggerDisposition.Queued&&r.Event.SequenceId.StartsWith("ring-exits")).Trigger.Text);
        t.Publish(p.Build("s","r",Now,301,[],true,false,false,s));
        Assert.Equal(2,s.Update(t,Now,10).Count(r=>r.Disposition==TriggerDisposition.Ended));
    }

    [Fact]
    public void AFrameCrossingAllPointsEndsAfterItsEarlierTriggers()
    {
        var t=new GuidanceTimeline();var s=new SpeechTriggerScheduler();t.Publish(Plan(200));
        var result=s.Update(t,Now,0);Assert.Equal(TriggerDisposition.Ended,result[^1].Disposition);
        Assert.DoesNotContain(result,r=>r.Disposition==TriggerDisposition.Queued);
    }

    private sealed class NoPlayer:IGuidanceAudioPlayer
    {public int Started;public Task PlayAsync(string path,CancellationToken token){Interlocked.Increment(ref Started);return Task.CompletedTask;}}
    [Fact]
    public async Task AnActiveAtomicBatchCannotStartAnItemWhoseEndWasCrossedInThatBatch()
    {
        var player=new NoPlayer();await using var manager=new GuidancePlaybackManager(null,player,()=>"ready.wav");
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Played+=s=>{if(s.Text=="fresh")finished.TrySetResult();};
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","old","old",0,0,"old")),false);
        manager.BeginUpdate();
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","old","old",0,0,"old"),ended:true),true);
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","fresh","fresh",0,0,"fresh")),true);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.Equal(1,player.Started);
    }
    [Fact]
    public async Task BatchEndRemovesPendingBeforePlaybackCanStartAndEventWithdrawalDoesNotEraseASharedSeries()
    {
        var player=new NoPlayer();await using var manager=new GuidancePlaybackManager(null,player,()=>"ready.wav");
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","first","one",1,0,"one"){SpeechSequenceId="exits"}),false);
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","last","two",2,0,"two"){SpeechSequenceId="exits"}),false);
        manager.Withdraw("navigation",["first"]);Assert.Equal("two",Assert.Single(manager.Pending).Text);
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","entry","entry",0,10,"entry")),false);Assert.Equal(2,manager.Pending.Count);
        manager.ApplyBatch(GuidanceTestSeries.Due(new("navigation","last","two",2,0,"two"){SpeechSequenceId="exits"},ended:true),false);
        Assert.Equal("entry",Assert.Single(manager.Pending).Text);Assert.Equal(0,player.Started);
    }

    [Fact]
    public void CameraVicinityIsNotPassageAndNoRouteSignedPassageFinishesOnlyTheCurrentEncounter()
    {
        var e=new CameraWarningEngine();
        WarningSnapshot Sample(CameraApproach a,double distance,double? along)=>new("s","r",false,true,Now,[new("c",a,distance,along)]);
        e.GetPlans(Sample(CameraApproach.UnavoidableAhead,10,10),Now);Assert.Empty(e.PassedEncounters);
        e.GetPlans(Sample(CameraApproach.Unknown,3,null),Now);Assert.Empty(e.PassedEncounters);
        e.GetPlans(Sample(CameraApproach.Behind,0,0),Now);Assert.Empty(e.PassedEncounters);
        e.GetPlans(Sample(CameraApproach.Behind,2,-2),Now);Assert.Equal(0,Assert.Single(e.PassedEncounters).EncounterRevision);
        e.GetPlans(Sample(CameraApproach.Behind,60,-60),Now);
        var plans=e.GetPlans(Sample(CameraApproach.UnavoidableAhead,40,40),Now);
        Assert.Equal(1,Assert.Single(plans).EncounterRevision);
    }
}
