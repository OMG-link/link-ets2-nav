using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class StraightJunctionPipelineTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static NavigationManeuver M(string id,ManeuverKind kind,double distance,double? end)
        =>new(id,kind,distance,"in","out"){JunctionId=id,EndDistanceMeters=end};
    private static (string,SpeechTriggerKind,double,bool,double?,int,double,bool) Shape(SpeechTrigger t)
        =>(t.Id,t.Kind,t.AlongMeters,t.StrictCrossing,t.SkipAtSpeedKph,t.Priority,t.SequenceOrder,t.Enabled);

    [Theory]
    [InlineData(2500)][InlineData(700)][InlineData(388)][InlineData(210)][InlineData(158)]
    [InlineData(128)][InlineData(55)][InlineData(25)][InlineData(10)]
    public void StraightAndLeftTurnHaveIdenticalPlannedPointsAndFollowingActionBoundaries(double distance)
    {
        GuidanceSourcePlan Plan(ManeuverKind kind)=>new NavigationEventPlanner().Build("s","r",Now,0,
            [M("action",kind,distance,distance+50),M("next",ManeuverKind.TurnRight,distance+600,distance+650)],true,false,false,new());
        var straight=Plan(ManeuverKind.Straight);var turn=Plan(ManeuverKind.TurnLeft);
        Assert.Equal(turn.Triggers.Select(Shape),straight.Triggers.Select(Shape));
        Assert.Equal(turn.Sequences,straight.Sequences);
        Assert.DoesNotContain(straight.Triggers,t=>t.Kind==SpeechTriggerKind.Passage);
        Assert.Contains(straight.Triggers,t=>t.EventId.StartsWith("action@")&&t.Text.Contains("直行"));
        Assert.Equal(distance+50,Assert.Single(straight.Triggers,t=>t.EventId.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
    }

    [Theory]
    [InlineData(ManeuverKind.Straight)][InlineData(ManeuverKind.TurnLeft)]
    public void EveryPointWaitsForThePreviousPhysicalEndIncludingExecution(ManeuverKind kind)
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=p.Build("s","r",Now,0,[M("previous",ManeuverKind.TurnRight,40,90),M("action",kind,100,140)],true,false,false,scheduler);
        var cue=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("action@"));
        Assert.Equal(SpeechTriggerKind.Execution,cue.Kind);Assert.Equal(90,cue.AlongMeters);Assert.True(cue.StrictCrossing);
        timeline.Publish(plan with{PlayerAlongMeters=90});
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Event.Id.StartsWith("action@")&&r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(plan with{PlayerAlongMeters=91});
        Assert.Equal(NavigationPrompts.Execute(kind),Assert.Single(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
    }

    [Theory]
    [InlineData(ManeuverKind.Straight)][InlineData(ManeuverKind.TurnLeft)]
    public void AdvanceStagesAreAlreadyBeyondTheFirstPointAndUnknownEndsAreNotGuessed(ManeuverKind kind)
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        var plan=p.Build("s","r",Now,0,[M("previous",ManeuverKind.TurnRight,100,null),M("action",kind,700,750)],true,false,false,scheduler);
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId.StartsWith("action@"));
        plan=p.Build("s","r",Now,0,[M("previous",ManeuverKind.TurnRight,100,150),M("action",kind,700,750)],true,false,false,scheduler);
        var points=plan.Triggers.Where(t=>t.EventId.StartsWith("action@")).ToArray();
        Assert.All(points,t=>Assert.True(t.AlongMeters>=150));
        Assert.All(points.Where(t=>t.Kind==SpeechTriggerKind.Advance),t=>Assert.True(t.AlongMeters>150));
        Assert.True(Assert.Single(points,t=>t.Kind==SpeechTriggerKind.Overview).StrictCrossing);
    }

    [Theory]
    [InlineData(ManeuverKind.Straight,140)][InlineData(ManeuverKind.Straight,150)]
    [InlineData(ManeuverKind.TurnLeft,140)][InlineData(ManeuverKind.TurnLeft,150)]
    public void APreviousEndAtOrAfterThisActionsEndLeavesNoLateInstruction(ManeuverKind kind,double previousEnd)
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=p.Build("s","r",Now,0,[M("previous",ManeuverKind.TurnRight,40,previousEnd),M("action",kind,100,140)],true,false,false,scheduler);
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId.StartsWith("action@"));
        timeline.Publish(plan with{PlayerAlongMeters=151});
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Event.Id.StartsWith("action@")&&r.Disposition==TriggerDisposition.Ended);
    }

    [Theory]
    [InlineData(ManeuverKind.Straight)][InlineData(ManeuverKind.TurnLeft)]
    public void HundredMeterAudioIsPreparedButHighSpeedSkipsOnlyThatPoint(ManeuverKind kind)
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=p.Build("s","r",Now,0,[M("action",kind,200,250)],true,false,false,scheduler);
        timeline.Publish(plan);scheduler.Update(timeline,Now,0);
        Assert.Contains(NavigationPrompts.AtDistance(kind,100),scheduler.Prepare(timeline,Now));
        timeline.Publish(plan with{PlayerAlongMeters=100});
        Assert.Equal(TriggerDisposition.SpeedSkipped,Assert.Single(scheduler.Update(timeline,Now,60/3.6)).Disposition);
        timeline.Publish(plan with{PlayerAlongMeters=180});
        Assert.Equal(NavigationPrompts.Execute(kind),Assert.Single(scheduler.Update(timeline,Now,60/3.6)).Trigger.Text);
    }

    [Fact]
    public void MainRoadContinuationStaysQuietAndEnvironmentCannotBlockTheNavigationChain()
    {
        var p=new NavigationEventPlanner();
        var main=M("main",ManeuverKind.Straight,100,400) with{IsMainRoadContinuation=true};
        var environment=M("narrow",ManeuverKind.MergeLeft,150,500) with{AnnouncementMode=NavigationAnnouncementMode.DistancePreview};
        var plan=p.Build("s","r",Now,0,[main,environment,M("action",ManeuverKind.Straight,300,350)],true,false,false,new());
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId.StartsWith("main@"));
        Assert.Equal(0,Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("action@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
    }

    [Fact]
    public void ComponentChildrenExecuteInsideTheirSharedEndAndTheNextIndependentActionWaits()
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var input=new[]{M("component",ManeuverKind.EnterRoundabout,100,300),
            M("inside",ManeuverKind.PassRoundaboutExit,160,300) with{JunctionId="component",ExecutionOnly=true,SpeechSequenceId="inside-series",SpeechSequenceOrder=1},
            M("exit",ManeuverKind.ExitRoundabout,260,300) with{JunctionId="component",ExecutionOnly=true,SpeechSequenceId="inside-series",SpeechSequenceOrder=2},
            M("next",ManeuverKind.Straight,700,750)};
        var plan=p.Build("s","r",Now,0,input,true,false,false,scheduler);
        Assert.Equal(140,Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("inside@")&&t.Kind==SpeechTriggerKind.Execution).AlongMeters);
        Assert.Equal(300,Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
        timeline.Publish(plan with{PlayerAlongMeters=140});
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Event.Id.StartsWith("inside@")&&r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(plan with{PlayerAlongMeters=300});
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Event.Id.StartsWith("next@")&&r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(plan with{PlayerAlongMeters=301});
        Assert.Equal("四百米后，直行。",Assert.Single(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
    }

    [Fact]
    public void StraightUsesTheSameNearbyActionCombinationAsAnyOtherNormalAction()
    {
        var plan=new NavigationEventPlanner().Build("s","r",Now,0,
            [M("first",ManeuverKind.Straight,20,50),M("next",ManeuverKind.Straight,80,110)],true,false,false,new());
        Assert.Equal("直行，然后六十米直行。",Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("first@")&&t.Kind==SpeechTriggerKind.Execution).Text);
    }
}
