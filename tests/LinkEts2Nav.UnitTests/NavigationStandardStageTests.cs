using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationStandardStageTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static NavigationManeuver Turn(double d)=>new("right",ManeuverKind.TurnRight,d,"in","out"){EndDistanceMeters=d+50};
    [Fact]
    public void UnreadyStandardRemainsPendingUntilReadyOrCoveredByANewerPoint()
    {
        var p=new NavigationInstructionPolicy();p.Update(Turn(1500),"s",Now,Now,true);
        Assert.Empty(p.Update(Turn(500),"s",Now,Now,true,_=>false));
        Assert.Equal("五百米后，右转。",Assert.Single(p.Update(Turn(400),"s",Now,Now,true)));
        Assert.Equal("两百米后，右转。",Assert.Single(p.Update(Turn(199),"s",Now,Now,true)));
        Assert.Equal("右转。",Assert.Single(p.Update(Turn(20),"s",Now,Now,true)));
    }
    [Fact]
    public void NoAdditionalEightHundredMeterBriefingAppearsBetweenStandards()
    {
        var p=new NavigationInstructionPolicy();p.Update(Turn(1500),"s",Now,Now,true);
        Assert.Empty(p.Update(Turn(1000),"s",Now,Now,true)); // Initial 1500m action preview skips its nearest lower stage.
        foreach(double distance in new[]{900d,800,750,600,501})Assert.Empty(p.Update(Turn(distance),"s",Now,Now,true));
        Assert.Equal("五百米后，右转。",Assert.Single(p.Update(Turn(500),"s",Now,Now,true)));
    }
    [Fact]
    public void PreparingALaterSampleDoesNotConsumeItsCrossedStandard()
    {
        var p=new NavigationInstructionPolicy();p.Update(Turn(2000),"s",Now,Now,true);
        p.Plan(Turn(995),"s",Now,Now,true);
        Assert.Equal("一公里后，右转。",Assert.Single(p.Update(Turn(995),"s",Now,Now,true)));
    }
}
