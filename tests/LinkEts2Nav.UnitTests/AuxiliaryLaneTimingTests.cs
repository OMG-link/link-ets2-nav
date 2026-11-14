using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class AuxiliaryLaneTimingTests
{
    [Theory]
    [InlineData(0,100,10)]
    [InlineData(0,200,20)]
    [InlineData(0,222.222,22.2222)]
    [InlineData(0,222.223,22.223)]
    [InlineData(1000,1500,1300)]
    [InlineData(-500,0,-200)]
    [InlineData(-600,-100,-300)]
    [InlineData(-50,50,-40)]
    // Real shared acceleration/deceleration corridor retained from replay 2026-11-14-143639.
    [InlineData(414.8808283046385,498.20971815144026,423.21371728931865)]
    public void TenPercentAnchorIsCappedRelativeToTheOriginalActionPoint(double start,double end,double expected)
    {
        var interval=new LaneActionInterval(start,end);
        Assert.Equal(expected,AuxiliaryLaneTiming.ActionPoint(interval),6);
        Assert.Equal(start+(end-start)*.1,interval.AtFraction(.1),9);
        Assert.InRange(end-AuxiliaryLaneTiming.ActionPoint(interval),0,200);
    }

    [Fact]
    public void ExactTransitionAndRouteTranslationUseTheSameRule()
    {
        double length=200/.9;
        foreach(double start in new[]{-1000d,0,1234})
            Assert.Equal(start+length*.1,AuxiliaryLaneTiming.ActionPoint(new(start,start+length)),9);
    }

    [Fact]
    public void CappedAnchorStillUsesNormalPreviewStagesAndTheOriginalEnd()
    {
        const double end=1200;var interval=new LaneActionInterval(700,end);
        var action=new NavigationManeuver("ramp",ManeuverKind.MergeLeft,AuxiliaryLaneTiming.ActionPoint(interval),"in","out")
            {EndDistanceMeters=end,ApproachInterval=interval};
        var plan=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,[action],true,false,false,new SpeechTriggerScheduler(),"f");
        Assert.Equal(1000,Assert.Single(plan.Events).AlongMeters);
        Assert.Equal(end,Assert.Single(plan.Sequences).EndAlongMeters);
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Advance&&t.AlongMeters==500);
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Advance&&t.AlongMeters==800);
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Execution&&t.AlongMeters==980);
    }
}
