using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.UnitTests;

public sealed class OneKilometerPreviewTests
{
    [Theory]
    [InlineData(ManeuverKind.TurnLeft)]
    [InlineData(ManeuverKind.Destination)]
    public void DistantDiscoveryKeepsRoadOverviewButActionPreviewsStartAtOneKilometer(ManeuverKind kind)
    {
        var now=DateTimeOffset.UtcNow;var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var action=new NavigationManeuver("target",kind,2500,"in","out"){EndDistanceMeters=2550};
        var plan=new NavigationEventPlanner().Build("s","r",now,0,[action],true,false,false,scheduler);
        Assert.Equal("沿当前道路，继续行驶二点五公里。",Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Overview).Text);
        Assert.Equal(new[]{1000d,500,200,100},plan.Triggers.Where(t=>t.Kind==SpeechTriggerKind.Advance).Select(t=>2500-t.AlongMeters).ToArray());
        timeline.Publish(plan);scheduler.Update(timeline,now,0);
        timeline.Publish(plan with{PlayerAlongMeters=500});
        Assert.DoesNotContain(scheduler.Update(timeline,now,0),r=>r.Disposition==TriggerDisposition.Queued);
        timeline.Publish(plan with{PlayerAlongMeters=1500});
        Assert.Equal(NavigationPrompts.AtDistance(kind,1000),Assert.Single(scheduler.Update(timeline,now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
    }

    [Theory]
    [InlineData(1000,"一公里后，左转。")]
    [InlineData(1100,"沿当前道路，继续行驶一点一公里。")]
    [InlineData(1500,"沿当前道路，继续行驶一点五公里。")]
    public void FirstLayerUsesTheSameOneKilometerBoundary(double distance,string expected)
        =>Assert.Equal(expected,NavigationPrompts.FirstLayer(ManeuverKind.TurnLeft,distance,0));
}
