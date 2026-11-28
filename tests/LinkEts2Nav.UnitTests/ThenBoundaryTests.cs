using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.UnitTests;

public sealed class ThenBoundaryTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver First(double end)=>new("first",ManeuverKind.KeepRight,100,"a","b")
        {JunctionId="first",EndDistanceMeters=end};
    private static NavigationManeuver Next(double distance)=>new("next",ManeuverKind.KeepRight,distance,"c","d")
        {JunctionId="next",EndDistanceMeters=distance+50};
    private static SpeechTrigger Execution(GuidanceSourcePlan plan)
        =>Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Execution&&t.EventId.StartsWith("first@"));
    private static GuidanceSourcePlan Plan(double end,double next)
        =>new NavigationEventPlanner().Build("s","r",Now,0,[First(end),Next(next)],true,false,false,new());

    [Fact]
    public void EarlyExecutionUsesRealEndForEligibilityButRealActionGapForSpokenDistance()
    {
        var plan=Plan(200.174428606,282.274874294);
        Assert.Equal("向右前方行驶，然后一百八十米向右前方行驶。",Execution(plan).Text);
        Assert.Equal(200.174428606,Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Overview&&t.EventId.StartsWith("next@")).AlongMeters);
    }
    [Theory]
    [InlineData(100.1,false)]
    [InlineData(100,false)]
    [InlineData(99.9,true)]
    public void ThresholdUsesThePhysicalExit(double available,bool joined)
        =>Assert.Equal(joined,Execution(Plan(250,250+available)).Text.Contains("然后"));

    [Theory]
    [InlineData(99.9, "向右前方行驶，然后一百九十米向右前方行驶。")]
    [InlineData(100, "向右前方行驶。")]
    public void LateDiscoveryRetainsItsFirstBriefingButCannotBridgeADistantExit(double afterExit, string expected)
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        planner.Build("s","r",Now,0,[First(200)],true,false,false,scheduler);
        // Refinement discovers the following decision after its predecessor's end.
        var plan=planner.Build("s","r",Now.AddSeconds(1),220,
            [First(200) with{DistanceMeters=-120,EndDistanceMeters=-20},Next(200 + afterExit - 220)],
            true,false,false,scheduler);
        Assert.Equal(220,Assert.Single(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Overview&&t.EventId.StartsWith("next@")).AlongMeters);
        Assert.Equal(expected, Execution(plan).Text);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(10060)]
    public void NearbyExitKeepsBothExecutionsIndependentWhenTheSpokenDistanceIsOutOfRange(double startToStart)
    {
        double next = 100 + startToStart;
        var plan = Plan(next - 30, next);
        Assert.Equal("向右前方行驶。", Execution(plan).Text);
        Assert.Equal("向右前方行驶。", Assert.Single(plan.Triggers, t => t.Kind == SpeechTriggerKind.Execution
            && t.EventId.StartsWith("next@")).Text);
    }
}
