using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationWordingTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver Turn(double d)=>new("turn",ManeuverKind.TurnLeft,d,"in","out"){JunctionId="turn",EndDistanceMeters=d+30};
    private static NavigationManeuver Straight(double d)=>new("straight",ManeuverKind.Straight,d,"in","out"){JunctionId="straight",EndDistanceMeters=d+30,StraightExitSides=NavigationSide.Left|NavigationSide.Right};
    [Fact]
    public void ImmediateNextIntersectionDoesNotNeedOrdinalPrefix()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("一百米后，左转。",Assert.Single(p.Update(Turn(128),"s",Now,Now,true)));
    }
    [Fact]
    public void OrdinaryOrdinalAppearsAtItsFixedTwoHundredMeterPoint()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("五百米后，左转。",Assert.Single(p.UpdateSequence([Straight(350),Turn(500)],"s",Now,Now,true)));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(p.UpdateSequence([Straight(50),Turn(200)],"s",Now,Now,true)));
        Assert.Equal("一百米后，左转。",Assert.Single(p.UpdateSequence([Turn(100)],"s",Now,Now,true)));
        Assert.Equal("左转。",Assert.Single(p.UpdateSequence([Turn(20)],"s",Now,Now,true)));
    }
    [Fact]
    public void LongRoadOverviewAndOrdinaryRemindersDoNotInventAnEarlyOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal(NavigationPrompts.Continue(2500),Assert.Single(p.UpdateSequence([Straight(2350),Turn(2500)],"s",Now,Now,true)));
        Assert.Equal("一公里后，左转。",Assert.Single(p.UpdateSequence([Straight(850),Turn(1000)],"s",Now,Now,true)));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(p.UpdateSequence([Straight(50),Turn(200)],"s",Now,Now,true)));
    }
}
