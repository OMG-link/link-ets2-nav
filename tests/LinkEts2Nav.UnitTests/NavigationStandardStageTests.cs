using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationStandardStageTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver Turn(double d)=>new("right",ManeuverKind.TurnRight,d,"in","out");
    private static NavigationManeuver Straight(double d)=>new("straight",ManeuverKind.Straight,d,"in","out");
    [Fact]
    public void AlreadyBriefedTurnAllowsStraightPassageCueBeforeReminderCompetition()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([Straight(1000),Turn(1500)],"s",Now,Now,true);
        var later=Now.AddSeconds(20);
        Assert.Equal("保持直行。",Assert.Single(p.UpdateSequence([Straight(100),Turn(560)],"s",later,later,true)));
        later=later.AddSeconds(2);
        Assert.Equal("五百米后，在第二个路口右转。",Assert.Single(p.UpdateSequence([Straight(40),Turn(500)],"s",later,later,true)));
    }

    [Fact]
    public void StraightPassageGetsItsOwnCueWithoutLosingTheConcurrentStandardReminder()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([Straight(600),Turn(1500)],"s",Now,Now,true);
        var later=Now.AddSeconds(20);
        Assert.Equal("保持直行。",Assert.Single(p.UpdateSequence([Straight(49),Turn(499)],"s",later,later,true)));
        later=later.AddMilliseconds(200);
        Assert.Equal("五百米后，在第二个路口右转。",Assert.Single(p.UpdateSequence([Straight(44),Turn(494)],"s",later,later,true)));
        later=later.AddSeconds(2);
        Assert.Empty(p.UpdateSequence([Turn(400)],"s",later,later,true));
    }

    [Fact]
    public void PassingStraightDoesNotResetStandardsOrInventFourHundredMeterCatchup()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([Straight(800),Turn(1500)],"s",Now,Now,true);
        var km=p.UpdateSequence([Straight(300),Turn(1000)],"s",Now,Now,true);
        Assert.Equal("一公里后，在第二个路口右转。",Assert.Single(km));
        Assert.Empty(p.UpdateSequence([Straight(60),Turn(501)],"s",Now,Now,true));
        Assert.Equal("五百米后，右转，前方路口直行。",Assert.Single(p.UpdateSequence([Straight(49),Turn(499)],"s",Now,Now,true)));
        Assert.Empty(p.UpdateSequence([Turn(400)],"s",Now,Now,true));
        Assert.Equal("两百米后，右转。",Assert.Single(p.UpdateSequence([Turn(200)],"s",Now,Now,true)));
    }
    [Fact]
    public void UnreadyBoundaryIsSkippedWithoutConsumingLaterBoundaries()
    {
        var p=new NavigationInstructionPolicy();
        p.Update(Turn(1500),"s",Now,Now,true);
        Assert.Empty(p.Update(Turn(500),"s",Now,Now,true,_=>false));
        Assert.Empty(p.Update(Turn(400),"s",Now,Now,true));
        Assert.Equal("两百米后，右转。",Assert.Single(p.Update(Turn(199),"s",Now,Now,true)));
        Assert.Equal("右转。",Assert.Single(p.Update(Turn(20),"s",Now,Now,true)));
    }
    [Fact]
    public void CurrentManeuverAudioPrecedesSpeculativeNextManeuverAudio()
    {
        var p=new NavigationInstructionPolicy();p.Update(Turn(1500),"s",Now,Now,true);
        var later=new NavigationManeuver("later",ManeuverKind.TurnLeft,900,"in","out");
        var plan=p.PlanSequence([Turn(650),later],"s",Now,Now,true).ToList();
        Assert.True(plan.IndexOf("五百米后，右转。")<plan.IndexOf("两百五十米后，左转。"));
    }
}
