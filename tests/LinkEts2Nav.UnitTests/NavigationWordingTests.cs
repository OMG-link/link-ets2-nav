using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationWordingTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver Turn(double d)=>new("turn",ManeuverKind.TurnLeft,d,"in","out");
    private static NavigationManeuver Straight(double d)=>new("straight",ManeuverKind.Straight,d,"in","out");
    [Fact]
    public void ImmediateNextIntersectionDoesNotNeedOrdinalPrefix()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("一百米后，左转。",Assert.Single(p.Update(Turn(128),"s",Now,Now,true)));
    }
    [Fact]
    public void OnlyFirstActionBriefingUsesOrdinalAndSubsequentRemindersUseRemainingStraightContext()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("五百米后，在第二个路口左转。",Assert.Single(p.UpdateSequence([Straight(100),Turn(500)],"s",Now,Now,true)));
        Assert.Equal("两百米后，左转，前方路口直行。",Assert.Single(p.UpdateSequence([Straight(60),Turn(200)],"s",Now,Now,true)));
        Assert.Equal("一百米后，左转。",Assert.Single(p.UpdateSequence([Turn(100)],"s",Now,Now,true)));
        Assert.Equal("左转。",Assert.Single(p.UpdateSequence([Turn(20)],"s",Now,Now,true)));
    }
    [Fact]
    public void RoadOverviewDoesNotCountAsTheFirstActionBriefing()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([Straight(1000),Turn(1500)],"s",Now,Now,true);
        Assert.Equal("一公里后，在第二个路口左转。",Assert.Single(p.UpdateSequence([Straight(300),Turn(1000)],"s",Now,Now,true)));
        Assert.Equal("五百米后，左转，前方路口直行。",Assert.Single(p.UpdateSequence([Straight(100),Turn(500)],"s",Now,Now,true)));
    }
}
