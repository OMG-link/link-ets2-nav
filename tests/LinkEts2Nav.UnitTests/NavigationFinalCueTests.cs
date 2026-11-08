using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationFinalCueTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static NavigationManeuver M(double distance)=>new("turn",ManeuverKind.TurnLeft,distance,"in","out");
    [Theory]
    [InlineData(25,null,false)]
    [InlineData(55,"五十米后，左转。",false)]
    [InlineData(88,"五十米后，左转。",false)]
    [InlineData(128,"一百米后，左转。",false)]
    [InlineData(158,"一百五十米后，左转。",false)]
    [InlineData(210,"两百米后，左转。",true)]
    [InlineData(388,"三百五十米后，左转。",true)]
    public void InitialBriefingNeverConsumesFinalTwentyMeterExecution(double first,string? initial,bool hundred)
    {
        var p=new NavigationInstructionPolicy();
        var output=p.Update(M(first),"session",Now,Now,true);
        if(initial is null)Assert.Empty(output);else Assert.Equal(initial,Assert.Single(output));
        if(first>100)
        {
            var next=p.Update(M(100),"session",Now.AddSeconds(1),Now.AddSeconds(1),true);
            if(hundred)Assert.Equal("一百米后，左转。",Assert.Single(next));else Assert.Empty(next);
        }
        Assert.Empty(p.Update(M(25),"session",Now.AddSeconds(2),Now.AddSeconds(2),true));
        Assert.Contains("左转。",p.Plan(M(25),"session",Now.AddSeconds(2),Now.AddSeconds(2),true));
        Assert.Equal("左转。",Assert.Single(p.Update(M(20),"session",Now.AddSeconds(3),Now.AddSeconds(3),true)));
        Assert.Empty(p.Update(M(10),"session",Now.AddSeconds(4),Now.AddSeconds(4),true));
    }
    [Fact]
    public void JumpingPastHundredDoesNotReintroduceAnAutomaticFiftyMeterReminder()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Single(p.Update(M(210),"session",Now,Now,true));
        Assert.Empty(p.Update(M(55),"session",Now.AddSeconds(1),Now.AddSeconds(1),true));
        Assert.Empty(p.Update(M(25),"session",Now.AddSeconds(2),Now.AddSeconds(2),true));
        Assert.Equal("左转。",Assert.Single(p.Update(M(18),"session",Now.AddSeconds(3),Now.AddSeconds(3),true)));
    }

    [Fact]
    public void UnreadyExecutionIsNotConsumedByAnEarlierAnnouncementOrPlanning()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Single(p.Update(M(210),"session",Now,Now,true));
        Assert.Empty(p.Update(M(20),"session",Now.AddSeconds(1),Now.AddSeconds(1),true,_=>false));
        Assert.Contains("左转。",p.Plan(M(18),"session",Now.AddSeconds(2),Now.AddSeconds(2),true));
        Assert.Equal("左转。",Assert.Single(p.Update(M(18),"session",Now.AddSeconds(2),Now.AddSeconds(2),true)));
        Assert.Empty(p.Update(M(10),"session",Now.AddSeconds(3),Now.AddSeconds(3),true));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(5)]
    public void FirstDetectionAtExecutionDistanceUsesBareAction(double distance)
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("左转。",Assert.Single(p.Update(M(distance),"session",Now,Now,true)));
    }
}
