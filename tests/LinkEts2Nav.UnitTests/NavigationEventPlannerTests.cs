using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationEventPlannerTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-11T00:00:00Z");
    private static NavigationManeuver Turn(double distance,string id="turn")=>new(id,ManeuverKind.TurnLeft,distance,"in","out"){JunctionId=id,EndDistanceMeters=distance+50};
    [Theory]
    [InlineData(25,null)] [InlineData(55,"五十米后，左转。")] [InlineData(88,"五十米后，左转。")]
    [InlineData(128,"一百米后，左转。")] [InlineData(158,"一百五十米后，左转。")]
    [InlineData(210,"两百米后，左转。")] [InlineData(388,"三百五十米后，左转。")]
    public void LateDiscoveryCreatesAnImmediateBriefingAndIndependentExecutionPoint(double initial,string? briefing)
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        IReadOnlyList<GuidanceTriggerResult> Step(double distance)
        {
            timeline.Publish(planner.Build("s","r",Now,1000-distance,[Turn(distance)],true,false,false,scheduler));
            return scheduler.Update(timeline,Now,0);
        }
        var first=Step(initial).Where(r=>r.Disposition==TriggerDisposition.Queued).ToArray();
        if(briefing is null)Assert.Empty(first);else Assert.Equal(briefing,Assert.Single(first).Trigger.Text);
        Assert.Equal("左转。",Assert.Single(Step(20),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        Assert.Empty(Step(19));
    }
    [Theory]
    [InlineData(158,100,false)] [InlineData(210,100,true)] [InlineData(388,200,false)] [InlineData(388,100,true)]
    public void InitialBriefingOnlySkipsItsImmediateLowerStandard(double initial,double next,bool spoken)
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        t.Publish(p.Build("s","r",Now,0,[Turn(initial)],true,false,false,s));s.Update(t,Now,0);
        t.Publish(p.Build("s","r",Now,initial-next,[Turn(next)],true,false,false,s));
        Assert.Equal(spoken,s.Update(t,Now,0).Any(r=>r.Disposition==TriggerDisposition.Queued));
    }
    [Fact]
    public void FutureEventsAndTheirAudioExistBeforeTheyBecomeTheCurrentAction()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        t.Publish(p.Build("s","r",Now,0,[Turn(300),Turn(1400,"later")],true,false,false,s));
        Assert.Equal(2,t.Sources[0].Events.Count);
        Assert.Contains(t.Sources[0].Triggers,x=>x.EventId.StartsWith("later@")&&x.Kind==SpeechTriggerKind.Execution&&x.Enabled);
        Assert.Contains("五百米后，左转。",s.Prepare(t,Now));
        Assert.All(s.Update(t,Now,0),r=>Assert.StartsWith("turn@",r.Event.Id));
        t.Publish(p.Build("s","r",Now,300,[Turn(1100,"later")],true,true,false,s));
        Assert.DoesNotContain(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued&&r.Event.Id.StartsWith("later@"));Assert.NotEmpty(s.Prepare(t,Now));
        t.Publish(p.Build("s","r",Now,351,[Turn(1049,"later")],true,false,false,s));
        Assert.Equal("沿当前道路，继续行驶一公里。",Assert.Single(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
    }
    [Fact]
    public void FastHundredMeterPointStaysPreparedAndIsConsumedWithoutDroppingExecution()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        t.Publish(p.Build("s","r",Now,0,[Turn(200)],true,false,false,s));s.Update(t,Now,0);
        Assert.Contains("一百米后，左转。",s.Prepare(t,Now));
        t.Publish(p.Build("s","r",Now,100,[Turn(100)],true,false,false,s));
        Assert.Equal(TriggerDisposition.SpeedSkipped,Assert.Single(s.Update(t,Now,60/3.6)).Disposition);
        t.Publish(p.Build("s","r",Now,180,[Turn(20)],true,false,false,s));
        Assert.Equal("左转。",Assert.Single(s.Update(t,Now,60/3.6)).Trigger.Text);
    }
    [Fact]
    public void ExecutionIncludesTheNearbyNextActionWithoutAnotherAfterPrefix()
    {
        var planner=new NavigationEventPlanner();
        var plan=planner.Build("s","r",Now,0,[Turn(20),Turn(80,"second")],true,false,false,new());
        Assert.Equal("左转，然后六十米左转。",Assert.Single(plan.Triggers,t=>t.Id.StartsWith("turn@")&&t.Kind==SpeechTriggerKind.Execution).Text);
    }
    [Fact]
    public void RoundaboutExitHasOnlyExecutionAndMergeSilenceDoesNotHideInterveningStraight()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        NavigationManeuver[] maneuvers=[new("straight",ManeuverKind.Straight,40,"a","b"),
            new("merge",ManeuverKind.MergeLeft,400,"b","c"){AnnouncementMode=NavigationAnnouncementMode.Silent},
            new("exit",ManeuverKind.ExitRoundabout,600,"c","d"){ExecutionOnly=true}];
        t.Publish(p.Build("s","r",Now,0,maneuvers,true,false,false,s));
        Assert.Equal("保持直行。",Assert.Single(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        Assert.Single(t.Sources[0].Triggers,x=>x.EventId.StartsWith("exit@"));
    }

    [Fact]
    public void FutureBriefingCountsJunctionsAfterItsOwnAnchorBeforeThePlayerGetsThere()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        NavigationManeuver Straight(string id,double distance)=>new(id,ManeuverKind.Straight,distance,"in","out"){JunctionId=id,EndDistanceMeters=distance+20};
        var plan=planner.Build("s","r",Now,0,
            [Straight("first",70),Straight("second",130),Straight("third",270),Turn(460)],true,false,false,scheduler,
            initialBoundaryAlong:315);
        var briefing=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Kind==SpeechTriggerKind.Overview);
        Assert.Equal(315,briefing.AlongMeters);
        Assert.Equal("一百米后，左转。",briefing.Text);
        Assert.DoesNotContain("个路口",briefing.Text);
    }

    [Fact]
    public void EachAdvancePointHasItsOwnOrdinalAndPassedStraightDoesNotAddNearInstruction()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        NavigationManeuver Straight(string id,double distance,string? junction=null)=>new(id,ManeuverKind.Straight,distance,"in","out")
            {JunctionId=junction??id,EndDistanceMeters=distance+20};
        var plan=planner.Build("s","r",Now,0,
            [Straight("behind-200",200),Straight("ahead-200",400),Turn(550)],true,false,false,scheduler,initialBoundaryAlong:50);
        var advance=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Id.EndsWith(":200"));
        Assert.Equal(350,advance.AlongMeters);
        Assert.Equal("两百米后，在第二个路口左转。",advance.Text);
        var near=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Id.EndsWith(":100"));
        Assert.Equal("一百米后，左转。",near.Text);
        // The text is prepared once against its fixed point and does not shrink
        // its ordinal merely because the player approaches before that point.
        var moved=planner.Build("s","r",Now,300,
            [Straight("ahead-200",100),Turn(250)],true,false,false,scheduler);
        Assert.Equal(advance,Assert.Single(moved.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Id.EndsWith(":200")));
    }

    [Fact]
    public void NearPointStillMentionsAStraightJunctionThatIsAheadOfThatPoint()
    {
        var planner=new NavigationEventPlanner();
        var straight=new NavigationManeuver("straight",ManeuverKind.Straight,490,"in","out")
            {JunctionId="straight",EndDistanceMeters=510};
        var plan=planner.Build("s","r",Now,0,[straight,Turn(550)],true,false,false,new(),initialBoundaryAlong:50);
        Assert.Equal("一百米后，左转，前方路口直行。",
            Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Id.EndsWith(":100")).Text);
    }

    [Fact]
    public void CountUsesPhysicalJunctionIdentityAndIgnoresUnknownApproachesBeforeThePoint()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        NavigationManeuver Straight(string id,double distance,string junction,bool separate=true)=>new(id,ManeuverKind.Straight,distance,"in","out")
            {JunctionId=junction,HasSeparateApproachRoad=separate,EndDistanceMeters=distance+20};
        var plan=planner.Build("s","r",Now,0,
            [Straight("past",200,"past",false),Straight("one-lane",350,"shared"),Straight("other-lane",360,"shared"),Turn(550)],
            true,false,false,scheduler,initialBoundaryAlong:50);
        Assert.Equal("两百米后，在第二个路口左转。",
            Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("turn@")&&t.Id.EndsWith(":200")).Text);
    }

    [Fact]
    public void DedicatedForkChoosesLaneAtOpeningAndReleasesNextBriefingOnEntry()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        var fork=new NavigationManeuver("fork",ManeuverKind.ChooseLanes,100,"in","out")
            {EndDistanceMeters=300,Instruction="走最右侧车道",ForkInstruction=new(ManeuverKind.ChooseLanes,false,"走最右侧车道",[2],[2])};
        t.Publish(p.Build("s","r",Now,0,[fork,Turn(700,"next")],true,false,false,s));
        Assert.Contains("一百米后，走最右侧车道。",s.Prepare(t,Now));
        Assert.DoesNotContain(s.Prepare(t,Now),x=>x.Contains("个路口走"));
        s.Update(t,Now,0);
        t.Publish(p.Build("s","r",Now,80,[fork with{DistanceMeters=20,EndDistanceMeters=220},Turn(620,"next")],true,false,false,s));
        Assert.Equal("走最右侧车道。",Assert.Single(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        t.Publish(p.Build("s","r",Now,300,[Turn(400,"next")],true,false,false,s));
        Assert.DoesNotContain(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Ended||r.Disposition==TriggerDisposition.Queued);
        t.Publish(p.Build("s","r",Now,301,[Turn(399,"next")],true,false,false,s));
        var results=s.Update(t,Now,0);
        Assert.Contains(results,r=>r.Disposition==TriggerDisposition.Ended);
        Assert.Equal("四百米后，左转。",Assert.Single(results,r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
    }

    [Fact]
    public void LaterProofOfMainContinuationEndsOldGenericForkSpeechSeries()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        var fork=new NavigationManeuver("fork",ManeuverKind.ChooseLanes,100,"a","b")
            {EndDistanceMeters=200,Instruction="走左侧两车道",ForkInstruction=new(ManeuverKind.ChooseLanes,false,"走左侧两车道",[0,1],[0,1])};
        t.Publish(p.Build("s","r",Now,0,[fork],true,false,false,s));
        Assert.Contains(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued);
        var main=fork with{Kind=ManeuverKind.Straight,IsMainRoadContinuation=true,AnnouncementMode=NavigationAnnouncementMode.Silent,
            ForkInstruction=fork.ForkInstruction with{Silent=true}};
        t.Publish(p.Build("s","r",Now,1,[main],true,false,false,s));
        var result=s.Update(t,Now,0);
        Assert.Contains(result,r=>r.Disposition==TriggerDisposition.Ended);
        Assert.DoesNotContain(result,r=>r.Disposition==TriggerDisposition.Queued);
    }
}
