using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationSequenceWordingTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver M(string id,ManeuverKind kind,double distance)=>new(id,kind,distance,"in","out"){JunctionId=id};
    [Theory]
    [InlineData(55,"五十")]
    [InlineData(60,"六十")]
    [InlineData(79,"七十")]
    [InlineData(88.43,"八十")]
    public void CloseActionsArePreparedAndCombinedAtExecutionWithoutConsumingSecondCue(double gap,string spoken)
    {
        var policy=new NavigationInstructionPolicy();
        NavigationManeuver[] Sequence(double distance)=>[M("first",ManeuverKind.TurnLeft,distance),M("second",ManeuverKind.TurnRight,distance+gap)];
        string text="左转，然后"+spoken+"米右转。";
        Assert.Contains(text,policy.PlanSequence(Sequence(200),"s",Now,Now,true));
        policy.UpdateSequence(Sequence(200),"s",Now,Now,true);
        Assert.Equal(text,Assert.Single(policy.UpdateSequence(Sequence(20),"s",Now,Now,true)));
        Assert.Equal("右转。",Assert.Single(policy.UpdateSequence([M("second",ManeuverKind.TurnRight,20)],"s",Now,Now,true)));
    }
    [Fact]
    public void UnreadyCompoundCannotDelayCachedExecutionCue()
    {
        var p=new NavigationInstructionPolicy();
        NavigationManeuver[] actions=[M("first",ManeuverKind.TurnLeft,20),M("second",ManeuverKind.TurnRight,80)];
        Assert.Contains("左转。",p.PlanSequence(actions,"s",Now,Now,true));
        Assert.Equal("左转。",Assert.Single(p.UpdateSequence(actions,"s",Now,Now,true,text=>text=="左转。")));
    }

    [Fact]
    public void HundredMeterGapAtExecutionAndSilentActionsAreNotCombined()
    {
        var p=new NavigationInstructionPolicy();
        var first=M("first",ManeuverKind.TurnLeft,20);
        Assert.Equal("左转。",Assert.Single(p.UpdateSequence([first,M("second",ManeuverKind.TurnRight,120)],"s",Now,Now,true)));
        var q=new NavigationInstructionPolicy();
        Assert.Equal("左转。",Assert.Single(q.UpdateSequence([first,M("second",ManeuverKind.MergeLeft,70) with{AnnouncementMode=NavigationAnnouncementMode.Silent}],"s",Now,Now,true)));
    }
    [Fact]
    public void OrdinalOnlyAppearsAtTwoHundredOrFirstCloseBriefing()
    {
        var p=new NavigationInstructionPolicy();
        NavigationManeuver[] Sequence(double turn,double straight)=>[M("straight",ManeuverKind.Straight,straight),M("turn",ManeuverKind.TurnLeft,turn)];
        Assert.Equal("五百米后，左转。",Assert.Single(p.UpdateSequence(Sequence(500,250),"s",Now,Now,true)));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(p.UpdateSequence(Sequence(200,50),"s",Now,Now,true)));
        var q=new NavigationInstructionPolicy();
        Assert.Equal("一百五十米后，在第二个路口左转。",Assert.Single(q.UpdateSequence(Sequence(175,50),"s",Now,Now,true)));
    }
    [Fact]
    public void SkippingDistanceStageDoesNotSkipFirstCloseOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("两百五十米后，左转。",Assert.Single(p.UpdateSequence(
            [M("straight",ManeuverKind.Straight,100),M("turn",ManeuverKind.TurnLeft,275)],"s",Now,Now,true)));
        Assert.Contains("两百米后，在第二个路口左转。",p.PlanSequence(
            [M("straight",ManeuverKind.Straight,90),M("turn",ManeuverKind.TurnLeft,265)],"s",Now,Now,true));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(p.UpdateSequence(
            [M("straight",ManeuverKind.Straight,25),M("turn",ManeuverKind.TurnLeft,200)],"s",Now,Now,true)));
    }

    [Fact]
    public void PassedStraightJunctionIsNotMentionedInTwoHundredMeterCue()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([M("straight",ManeuverKind.Straight,250),M("turn",ManeuverKind.TurnLeft,500)],"s",Now,Now,true);
        Assert.Equal("两百米后，左转。",Assert.Single(p.UpdateSequence([M("turn",ManeuverKind.TurnLeft,200)],"s",Now,Now,true)));
    }
}
