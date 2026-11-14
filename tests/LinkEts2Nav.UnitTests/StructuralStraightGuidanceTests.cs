using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class StructuralStraightGuidanceTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    // Synthetic physical junctions occupy a known 30-metre interval.
    private static NavigationManeuver S(string id,double distance,bool minor=false,NavigationJunctionKind kind=NavigationJunctionKind.Ordinary)
        =>new(id,ManeuverKind.Straight,distance,"in","out")
            {JunctionId=id,EndDistanceMeters=distance+30,IsMinorStraight=minor,JunctionKind=kind};
    private static NavigationManeuver A(string id,double distance,NavigationJunctionKind kind=NavigationJunctionKind.Ordinary)
        =>new(id,ManeuverKind.TurnLeft,distance,"in","out"){JunctionId=id,EndDistanceMeters=distance+30,JunctionKind=kind};
    private static GuidanceSourcePlan Plan(params NavigationManeuver[] input)
        =>new NavigationEventPlanner().Build("s","r",Now,0,input,true,false,false,new());
    private static SpeechTrigger[] Assist(GuidanceSourcePlan plan)=>plan.Triggers.Where(t=>t.Kind==SpeechTriggerKind.ActionAssist).ToArray();
    [Fact]
    public void StraightIsStructuralAndDoesNotBlockTheNextActionOrThenWording()
    {
        var plan=Plan(A("first",100),S("straight",150),A("last",180));
        Assert.False(NavigationEventPlanner.IsNavigationAction(S("s",0)));
        Assert.Contains(plan.Events,e=>e.Id.StartsWith("straight@"));
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId.StartsWith("straight@"));
        Assert.Equal(130,Assert.Single(plan.Triggers,t=>t.Id.StartsWith("last@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
        Assert.Equal("左转，然后八十米左转。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("first@")&&t.Kind==SpeechTriggerKind.Execution).Text);
    }
    [Fact]
    public void OrdinaryCountsAtTwoHundredAndExcludesMinorAndTargetDuplicatePorts()
    {
        var plan=Plan(S("first",850),S("minor",900,true),S("ramp",950,minor:true,kind:NavigationJunctionKind.Ramp),
            S("target-straight",990) with{JunctionId="target"},A("target",1000));
        Assert.Equal("两百米后，在第三个路口左转。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("target@")&&t.Id.EndsWith(":200")).Text);
        var assists=Assist(plan);Assert.Equal(2,assists.Length);
        Assert.Equal(new[]{"直行通过第一个路口。","直行通过第二个路口。"},assists.Select(t=>t.Text));
        Assert.Equal(new double[]{830,930},assists.Select(t=>t.AlongMeters));
        Assert.All(assists,t=>Assert.Equal(10,t.Priority));
    }
    [Fact]
    public void RampCountsAtTwoHundredAndEarlierJunctionGetsAnUnnumberedHelper()
    {
        var plan=Plan(S("first",600),S("second",850),A("ramp",1000,NavigationJunctionKind.Ramp));
        Assert.Equal("五百米后，左转。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("ramp@")&&t.Id.EndsWith(":500")).Text);
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("ramp@")&&t.Id.EndsWith(":200")).Text);
        Assert.Equal(new[]{"直线通过当前路口。","直行通过第一个路口。"},Assist(plan).Select(t=>t.Text));
    }
    [Theory]
    [InlineData(150,NavigationJunctionKind.Ordinary,"一百五十米后，在第二个路口左转。")]
    [InlineData(450,NavigationJunctionKind.Ramp,"四百五十米后，左转。")]
    public void InitialPreviewInsideTheCountingRangeIncludesTheOrdinal(double distance,NavigationJunctionKind kind,string expected)
    {
        var plan=Plan(S("s",50),A("target",distance,kind));
        Assert.Equal(expected,Assert.Single(plan.Triggers,t=>t.Id.StartsWith("target@")&&t.Kind==SpeechTriggerKind.Overview).Text);
    }
    [Fact]
    public void ACountedStraightFirstDiscoveredWithinTwentyMetersGetsAnImmediateHelper()
    {
        var plan=Plan(S("s",10),A("target",150));
        Assert.Equal(0,Assert.Single(Assist(plan)).AlongMeters);
        var helper=Assert.Single(plan.Sequences,s=>s.Id.Contains(":through:"));Assert.Equal(40,helper.EndAlongMeters);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void CountStageOverridesNearestStageSkippingOnlyWhenThereIsAnIntermediateStraight(bool intermediate)
    {
        var plan=intermediate?Plan(S("s",250),A("target",388)):Plan(A("target",388));
        Assert.Equal(intermediate,plan.Triggers.Any(t=>t.Id.StartsWith("target@")&&t.Id.EndsWith(":200")));
    }
    [Fact]
    public void RoundaboutCountsIntermediateStraightsAndRetainsTheExitNumberAtTheNearStage()
    {
        var ring=A("ring",500) with{Kind=ManeuverKind.EnterRoundabout,Instruction="进入环岛",NearInstruction="进入环岛，请从第二出口驶出"};
        var plan=Plan(S("s",400),ring);
        Assert.Equal("两百米后，在第二个路口进入环岛。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("ring@")&&t.Id.EndsWith(":200")).Text);
        Assert.Equal("一百米后，进入环岛，请从第二出口驶出。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("ring@")&&t.Id.EndsWith(":100")).Text);
        Assert.Single(Assist(plan));
    }
    [Theory]
    [InlineData(3,330)][InlineData(4,430)][InlineData(5,530)]
    public void DenseSummaryIsAtFirstMinusTwoHundredAndEndsAfterTheThirdFromLast(int count,double end)
    {
        var plan=Plan(Enumerable.Range(0,count).Select(i=>S("s"+i,300+i*100)).ToArray());
        var trigger=Assert.Single(Assist(plan));Assert.Equal(100,trigger.AlongMeters);
        Assert.Equal("注意在前方多个路口保持直行。",trigger.Text);
        Assert.Equal(end,Assert.Single(plan.Sequences,s=>s.Id.StartsWith("straight-chain:")).EndAlongMeters);
    }
    [Fact]
    public void MinorJunctionsDoNotCreateAChainAndTwoHundredMeterGapsBreakIt()
    {
        Assert.Empty(Assist(Plan(S("a",300),S("minor",350,true),S("b",400))));
        Assert.Empty(Assist(Plan(S("a",300),S("b",500),S("c",600))));
    }
    [Fact]
    public void CountedStraightsDoNotAlsoFormADenseChain()
    {
        var plan=Plan(S("a",320),S("b",380),S("c",440),A("ramp",500,NavigationJunctionKind.Ramp));
        Assert.Equal(3,Assist(plan).Length);
        Assert.DoesNotContain(plan.Sequences,s=>s.Id.StartsWith("straight-chain:"));
    }
    [Fact]
    public void DenseSummaryIsNotCaughtUpIfTheThirdJunctionIsDiscoveredAfterItsPoint()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        planner.Build("s","r",Now,0,[S("a",300),S("b",400)],true,false,false,scheduler);
        var late=planner.Build("s","r",Now,150,[S("a",150),S("b",250),S("c",350)],true,false,false,scheduler);
        Assert.Empty(Assist(late));
        var again=planner.Build("s","r",Now,160,[S("b",240),S("c",340)],true,false,false,scheduler);
        Assert.Empty(Assist(again));
    }
    [Theory]
    [InlineData(70,true,true)][InlineData(100,false,false)]
    public void DenseFixedPointMustNotPrecedeThePreviousActionEnd(double previousDistance,bool present,bool strict)
    {
        var plan=Plan(A("previous",previousDistance),S("a",300),S("b",400),S("c",500));
        Assert.Equal(present,Assist(plan).Length==1);
        if(present)Assert.Equal(strict,Assist(plan)[0].StrictCrossing);
    }
    [Fact]
    public void UnknownPreviousEndOrAnInitialTurnBoundaryCannotProduceADenseSummary()
    {
        Assert.Empty(Assist(Plan(A("previous",50) with{EndDistanceMeters=null},S("a",300),S("b",400),S("c",500))));
        var p=new NavigationEventPlanner();
        var plan=p.Build("s","r",Now,0,[S("a",300),S("b",400),S("c",500)],true,false,false,new(),initialBoundaryAlong:150);
        Assert.Empty(Assist(plan));
    }
    [Fact]
    public void LaterCountingCancelsTheOldDenseSummaryRatherThanRetainingItInTheTimeline()
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        timeline.Publish(p.Build("s","r",Now,0,[S("a",300),S("b",400),S("c",500),A("far",1500)],true,false,false,scheduler,coordinateFrame:"f"));
        Assert.Contains(scheduler.Prepare(timeline,Now),s=>s.StartsWith("注意在前方多个"));
        timeline.Publish(p.Build("s","r",Now,10,[S("a",290),S("b",390),S("c",490),A("near",640),A("far",1490)],true,false,false,scheduler,coordinateFrame:"f"));
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Ended&&r.Event.Id.StartsWith("straight-chain:"));
        Assert.DoesNotContain(scheduler.Prepare(timeline,Now),s=>s.StartsWith("注意在前方多个"));
    }
    [Fact]
    public void LearningTheDenseSeriesEndAfterItsPointDoesNotCatchUpOrResurrectSpeech()
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        timeline.Publish(p.Build("s","r",Now,0,[S("a",300) with{EndDistanceMeters=null},S("b",400),S("c",500)],true,false,false,scheduler));
        Assert.Empty(Assist(timeline.Sources[0]));
        timeline.Publish(p.Build("s","r",Now,150,[S("a",150),S("b",250),S("c",350)],true,false,false,scheduler));
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(p.Build("s","r",Now,160,[S("a",140),S("b",240),S("c",340)],true,false,false,scheduler));
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued);
        Assert.DoesNotContain(scheduler.Prepare(timeline,Now),s=>s.StartsWith("注意在前方多个"));
    }
    [Fact]
    public void LearningThePreviousActionEndAfterTheDensePointDoesNotCatchUpItsSummary()
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        timeline.Publish(p.Build("s","r",Now,0,[A("previous",50) with{EndDistanceMeters=null},S("a",300),S("b",400),S("c",500)],true,false,false,scheduler));
        scheduler.Update(timeline,Now,0);
        timeline.Publish(p.Build("s","r",Now,150,[A("previous",-100),S("a",150),S("b",250),S("c",350)],true,false,false,scheduler));
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Trigger.Kind==SpeechTriggerKind.ActionAssist&&r.Disposition==TriggerDisposition.Queued);
    }
    [Fact]
    public void StartingInsideARoundaboutKeepsItsOwnExitCuesBeforeTheSharedExternalEnd()
    {
        var exit=new NavigationManeuver("ring-exit",ManeuverKind.ExitRoundabout,50,"ring","ring")
            {JunctionId="ring",EndDistanceMeters=200,SpeechSequenceId="ring",SpeechSequenceStartDistanceMeters=-100,
                SpeechSequenceOrder=1,ExecutionOnly=true};
        var plan=new NavigationEventPlanner().Build("s","r",Now,100,[exit,A("next",400)],true,false,false,new(),initialBoundaryAlong:300);
        Assert.Equal(130,Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("ring-exit")).AlongMeters);
        Assert.Equal(300,Assert.Single(plan.Triggers,t=>t.Id.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
        Assert.Equal(300,Assert.Single(plan.Sequences,s=>s.Id.StartsWith("ring:")).EndAlongMeters);
    }
    [Fact]
    public void NumberedHelperEndsStrictlyAfterItsOwnPhysicalJunction()
    {
        var p=Plan(S("s",100),A("target",150));var t=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        t.Publish(p with{PlayerAlongMeters=80});Assert.Contains(scheduler.Update(t,Now,0),r=>r.Trigger.Kind==SpeechTriggerKind.ActionAssist&&r.Disposition==TriggerDisposition.Queued);
        t.Publish(p with{PlayerAlongMeters=130});Assert.DoesNotContain(scheduler.Update(t,Now,0),r=>r.Event.Id.Contains(":through:")&&r.Disposition==TriggerDisposition.Ended);
        t.Publish(p with{PlayerAlongMeters=131});Assert.Contains(scheduler.Update(t,Now,0),r=>r.Event.Id.Contains(":through:")&&r.Disposition==TriggerDisposition.Ended);
    }
    [Theory]
    [InlineData(1000,"一公里后，左转。")][InlineData(2000,"两公里后，左转。")]
    [InlineData(2050,"沿当前道路，继续行驶两公里。")]
    public void OnlyDistancesOverTwoKilometersUseContinueDistance(double distance,string expected)
        =>Assert.Equal(expected,Assert.Single(Plan(A("target",distance)).Triggers,t=>t.Kind==SpeechTriggerKind.Overview).Text);
    [Fact]
    public void AnUnknownNextActionUsesTheActualConfirmedEndEvenWithStraightStructureAhead()
    {
        var p=new NavigationEventPlanner();var plan=p.Build("s","r",Now,0,[S("s",100)],true,false,true,new(),confirmedContinuationEndAlong:300);
        var continuation=Assert.Single(plan.Sequences,s=>s.Id.StartsWith("continuation:"));
        Assert.Equal(300,continuation.EndAlongMeters);Assert.Equal(NavigationPrompts.Continue(),Assert.Single(continuation.Triggers).Text);
        Assert.Equal(0,Assert.Single(continuation.Triggers).AlongMeters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothPassageHelpersUseTwoSecondWindowAndTheirOwnPhysicalEnd(bool numbered)
    {
        double site=numbered?850:600;
        var plan=Plan(S("s",site),A("target",1000));
        var helper=Assert.Single(Assist(plan));Assert.NotNull(helper.Window);
        Assert.Equal(numbered?"直行通过第一个路口。":"直线通过当前路口。",helper.Text);
        Assert.Equal(site-20,helper.AlongMeters);
        var series=Assert.Single(plan.Sequences,s=>s.Id.Contains(":through:"));Assert.Equal(site+30,series.EndAlongMeters);
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(plan with{PlayerAlongMeters=site-51});
        Assert.DoesNotContain(scheduler.Update(timeline,Now,25),r=>r.Trigger.Id==helper.Id&&r.Disposition==TriggerDisposition.Queued);
        timeline.UpdateProgress("navigation","s",site-49,Now,true);
        Assert.Contains(scheduler.Update(timeline,Now,25),r=>r.Trigger.Id==helper.Id&&r.Disposition==TriggerDisposition.Queued);
        timeline.UpdateProgress("navigation","s",site-10,Now,true);
        Assert.DoesNotContain(scheduler.Update(timeline,Now,25),r=>r.Trigger.Id==helper.Id&&r.Disposition==TriggerDisposition.Queued);
        timeline.UpdateProgress("navigation","s",site+31,Now,true);
        Assert.Contains(scheduler.Update(timeline,Now,25),r=>r.Event.Id==helper.EventId&&r.Disposition==TriggerDisposition.Ended);
    }

    [Fact]
    public void SkippingAnUnnumberedWindowStillForcesItsTwentyMeterDeadline()
    {
        var plan=Plan(S("s",600),A("target",1000));var helper=Assert.Single(Assist(plan));
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(plan with{PlayerAlongMeters=585});
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Trigger.Id==helper.Id&&r.Disposition==TriggerDisposition.Queued);
    }

    [Fact]
    public void EarlyPassagesAreNotNumberedAndDoNotAlsoCreateADenseSummary()
    {
        var plan=Plan(S("a",510),S("b",600),S("c",700),S("d",850),A("target",1000));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(plan.Triggers,t=>t.Id.EndsWith(":200")).Text);
        Assert.Equal(new[]{"直线通过当前路口。","直线通过当前路口。","直线通过当前路口。","直行通过第一个路口。"},Assist(plan).Select(t=>t.Text));
        Assert.DoesNotContain(plan.Sequences,s=>s.Id.StartsWith("straight-chain:"));
    }

    [Fact]
    public void InitialPreviewBetweenTwoHundredAndFiveHundredDoesNotMoveOrdinalAnnouncementForward()
    {
        var plan=Plan(S("early",50),S("counted",300),A("target",450,NavigationJunctionKind.Ramp));
        Assert.Equal("四百五十米后，左转。",Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Overview).Text);
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(plan.Triggers,t=>t.Id.EndsWith(":200")).Text);
    }

    [Fact]
    public void ARealPassageBeforeANonJunctionDestinationUsesNoOrdinal()
    {
        var destination=A("destination",1000) with{Kind=ManeuverKind.Destination,JunctionId=null};
        var plan=Plan(S("early",600),S("near",850),destination);
        Assert.Equal(new[]{"直线通过当前路口。","直线通过当前路口。"},Assist(plan).Select(t=>t.Text));
        Assert.DoesNotContain(plan.Triggers,t=>t.Text.Contains("第"));
    }

    [Fact]
    public void ChangingAPassageFromUnnumberedToNumberedDoesNotRepeatItsConsumedCue()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var first=planner.Build("s","r",Now,0,[S("s",600),A("target",1000)],true,false,false,scheduler,"frame");
        var original=Assert.Single(Assist(first));Assert.Equal("直线通过当前路口。",original.Text);
        timeline.Publish(first with{PlayerAlongMeters=580});
        Assert.Contains(scheduler.Update(timeline,Now,0),r=>r.Trigger.Id==original.Id&&r.Disposition==TriggerDisposition.Queued);
        var refined=planner.Build("s","r",Now,580,[S("s",20),A("target",170)],true,false,false,scheduler,"frame");
        var updated=Assert.Single(Assist(refined));Assert.Equal(original.Id,updated.Id);Assert.Equal("直行通过第一个路口。",updated.Text);
        timeline.Publish(refined);
        Assert.DoesNotContain(scheduler.Update(timeline,Now,0),r=>r.Trigger.Id==original.Id&&r.Disposition==TriggerDisposition.Queued);
    }

    [Fact]
    public void EarlyBandChoosesTheJunctionButDoesNotDelayItsCueUntilTheBandEntrance()
    {
        var helper=Assert.Single(Assist(Plan(S("s",500),A("target",1000))));
        Assert.Equal("直线通过当前路口。",helper.Text);
        Assert.Equal(480,helper.AlongMeters);Assert.Equal(0,helper.Window!.StartAlongMeters);
    }
}
