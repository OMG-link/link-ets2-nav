using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationSequenceWordingTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    // Constructed junctions in this wording fixture have known thirty-metre physical intervals.
    private static NavigationManeuver M(string id,ManeuverKind kind,double distance)=>new(id,kind,distance,"in","out"){JunctionId=id,EndDistanceMeters=distance+30};
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
    [Theory]
    [InlineData(0, "左转，然后立即右转。", "左转，然后到达目的地。")]
    [InlineData(10, "左转，然后立即右转。", "左转，然后到达目的地。")]
    [InlineData(20, "左转，然后立即右转。", "左转，然后到达目的地。")]
    [InlineData(20.1, "左转，然后二十米右转。", "左转，然后二十米到达目的地。")]
    [InlineData(29.9, "左转，然后二十米右转。", "左转，然后二十米到达目的地。")]
    public void ThenUsesTheUnroundedGap(double gap,string turn,string destination)
    {
        Assert.Equal(turn,NavigationPrompts.Then("左转。",ManeuverKind.TurnRight,gap));
        Assert.Equal(destination,NavigationPrompts.Then("左转。",ManeuverKind.Destination,gap));
    }
    [Theory]
    [InlineData(16, "左转，然后立即右转。", "左转，然后到达目的地。")]
    [InlineData(20, "左转，然后立即右转。", "左转，然后到达目的地。")]
    [InlineData(20.1, "左转，然后二十米右转。", "左转，然后二十米到达目的地。")]
    public void PlannerPreservesTheRealGapUntilWording(double gap,string turn,string destination)
    {
        string Execute(ManeuverKind next)
        {
            var plan=new LinkEts2Nav.Core.Guidance.NavigationEventPlanner().Build("s","r",Now,0,
                [M("first",ManeuverKind.TurnLeft,100),M("second",next,100+gap)],true,false,false,new());
            return Assert.Single(plan.Triggers,t=>t.Id.StartsWith("first@")&&t.Kind==LinkEts2Nav.Core.Guidance.SpeechTriggerKind.Execution).Text;
        }
        Assert.Equal(turn,Execute(ManeuverKind.TurnRight));
        Assert.Equal(destination,Execute(ManeuverKind.Destination));
    }
    [Fact]
    public void UnreadyCompoundWaitsForItsOwnPreparedAudio()
    {
        var p=new NavigationInstructionPolicy();
        NavigationManeuver[] actions=[M("first",ManeuverKind.TurnLeft,20),M("second",ManeuverKind.TurnRight,80)];
        const string compound="左转，然后六十米右转。";
        Assert.Contains(compound,p.PlanSequence(actions,"s",Now,Now,true));
        Assert.Empty(p.UpdateSequence(actions,"s",Now,Now,true,text=>text=="左转。"));
        Assert.Equal(compound,Assert.Single(p.UpdateSequence(actions,"s",Now,Now,true)));
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
    [Theory]
    [InlineData(500,2,"五百米后，在第二个路口左转。")]
    [InlineData(200,2,"两百米后，在第二个路口左转。")]
    [InlineData(100,0,"一百米后，左转。")]
    public void SharedPromptHonorsOnlyTheOrdinalProvidedByThePlannedPoint(int distance,int ordinal,string expected)
        =>Assert.Equal(expected,NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,distance,ordinal));
}
