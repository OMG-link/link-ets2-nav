using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.UnitTests;

public sealed class ActionPointConstraintsTests
{
    [Theory]
    [InlineData(ManeuverKind.KeepRight)]
    [InlineData(ManeuverKind.MergeLeft)]
    public void FinalActionWaitsForPreviousPrefabExitWithoutChangingRawIntervalOrTrueEnds(ManeuverKind kind)
    {
        const double entranceA=6284.507765293121,exitA=6329.175657510757;
        const double entranceB=6344.178223669529,exitB=6387.526071846485;
        var a=new NavigationManeuver("A",ManeuverKind.ChooseLanes,entranceA,"in","out")
            {JunctionId="prefab:A",EndDistanceMeters=exitA,Instruction="分岔口靠右，走右侧两车道"};
        var b=new NavigationManeuver("B",kind,6211.730084890127,"in","out")
            {JunctionId="prefab:B",EndDistanceMeters=exitB,ApproachInterval=new(6197.013625025749,entranceB)};
        var actions=ActionPointConstraints.Apply([b,a]);
        Assert.Equal(exitA,actions.Single(m=>m.Id=="B").DistanceMeters);
        Assert.Same(b.ApproachInterval,actions.Single(m=>m.Id=="B").ApproachInterval);
        Assert.Equal(exitB,actions.Single(m=>m.Id=="B").EndDistanceMeters);
        var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,actions,true,false,false,scheduler);
        Assert.Contains(plan.Triggers,t=>t.EventId.StartsWith("A@")&&t.Text.Contains("然后四十米"));
        Assert.DoesNotContain(plan.Triggers,t=>t.EventId.StartsWith("B@")&&t.AlongMeters<exitA);
        IReadOnlyList<GuidanceTriggerResult> Step(double along)
        {timeline.Publish(plan with{PlayerAlongMeters=along});return scheduler.Update(timeline,plan.ObservedAt,0);}
        foreach(var at in new[]{0,entranceA,exitA-1,exitA})
            Assert.DoesNotContain(Step(at),r=>r.Disposition==TriggerDisposition.Queued&&r.Event.Id.StartsWith("B@"));
        var crossed=Step(exitA+.001);
        Assert.Contains(crossed,r=>r.Disposition==TriggerDisposition.Ended&&r.Event.Id.StartsWith("A@"));
        Assert.Contains(crossed,r=>r.Disposition==TriggerDisposition.Queued&&r.Event.Id.StartsWith("B@"));
    }

    [Fact]
    public void RingChildrenAndEnvironmentKeepTheirIndependentScheduling()
    {
        var previous=new NavigationManeuver("previous",ManeuverKind.TurnLeft,50,"in","out"){EndDistanceMeters=100};
        var enter=new NavigationManeuver("enter",ManeuverKind.EnterRoundabout,80,"in","out")
            {SpeechSequenceId="ring",EndDistanceMeters=500};
        var pass=new NavigationManeuver("pass",ManeuverKind.PassRoundaboutExit,200,"in","out")
            {SpeechSequenceId="ring",EndDistanceMeters=500,ExecutionOnly=true};
        var cameraLike=new NavigationManeuver("environment",ManeuverKind.MergeLeft,90,"in","out")
            {EndDistanceMeters=400,AnnouncementMode=NavigationAnnouncementMode.DistancePreview};
        var straight=new NavigationManeuver("straight",ManeuverKind.Straight,150,"in","out"){EndDistanceMeters=180};
        var actions=ActionPointConstraints.Apply([previous,enter,pass,cameraLike,straight]);
        Assert.Equal(100,actions.Single(a=>a.Id=="enter").DistanceMeters);
        Assert.Equal(200,actions.Single(a=>a.Id=="pass").DistanceMeters);
        Assert.Equal(90,actions.Single(a=>a.Id=="environment").DistanceMeters);
        Assert.Equal(150,actions.Single(a=>a.Id=="straight").DistanceMeters);
    }
}
