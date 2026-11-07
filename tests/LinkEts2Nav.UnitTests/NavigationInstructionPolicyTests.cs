using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationInstructionPolicyTests
{
    private static readonly DateTimeOffset Now=new(2026,11,8,0,0,0,TimeSpan.Zero);
    private static NavigationManeuver M(double distance,string id="action",ManeuverKind kind=ManeuverKind.TurnLeft)
        =>new(id,kind,distance,"entry","exit");
    private static IReadOnlyList<string> Plan(NavigationInstructionPolicy p,IReadOnlyList<NavigationManeuver> m)
        =>p.PlanSequence(m,"session",Now,Now,true);
    private static IReadOnlyList<string> Update(NavigationInstructionPolicy p,IReadOnlyList<NavigationManeuver> m,
        Func<string,bool>? ready=null,int seconds=0)
        =>p.UpdateSequence(m,"session",Now.AddSeconds(seconds),Now.AddSeconds(seconds),true,ready);

    [Fact]
    public void CountsStraightIntersectionsBeforeActionAndDoesNotTreatStraightAsTarget()
    {
        var p=new NavigationInstructionPolicy();
        NavigationManeuver[] sequence=[M(100,"s1",ManeuverKind.Straight),M(200,"s2",ManeuverKind.Straight),M(650)];
        Assert.Equal("六百五十米后，在第三个路口左转。",Plan(p,sequence)[0]);
        Assert.Equal("六百五十米后，在第三个路口左转。",Assert.Single(Update(p,sequence)));
        Assert.Empty(Update(p,[M(190,"s2",ManeuverKind.Straight),M(640)]));
    }

    [Fact]
    public void PhysicalJunctionIdsAreCountedOnce()
    {
        var p=new NavigationInstructionPolicy();
        var s=M(100,"straight",ManeuverKind.Straight) with { JunctionId="physical" };
        var turn=M(500) with { JunctionId="physical" };
        Assert.Equal("五百米后，在下一个路口左转。",Assert.Single(Update(p,[s,turn])));
    }

    [Fact]
    public void ConsecutivePrefabWithoutIndependentApproachOmitsUnprovenOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        var s=M(100,"s",ManeuverKind.Straight) with { HasSeparateApproachRoad=false };
        Assert.Equal("五百米后，左转。",Assert.Single(Update(p,[s,M(500)])));
    }

    [Fact]
    public void FirstLongDistanceBriefingAndSubsequentStandardGradients()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("沿当前道路，继续行驶二点五公里。",Assert.Single(Update(p,[M(2500)])));
        Assert.Empty(Update(p,[M(2001)]));
        Assert.Equal("前方二千米，左转。",Assert.Single(Update(p,[M(1995)])));
        Assert.Empty(Update(p,[M(1001)]));
        Assert.Equal("前方一千米，左转。",Assert.Single(Update(p,[M(995)])));
        Assert.Equal("八百米后，在下一个路口左转。",Assert.Single(Update(p,[M(800)])));
        Assert.Empty(Update(p,[M(501)]));
        Assert.Empty(Update(p,[M(498)]));
        Assert.Empty(Update(p,[M(201)]));
        Assert.Equal("前方二百米，左转。",Assert.Single(Update(p,[M(198)])));
        Assert.Empty(Update(p,[M(51)]));
        Assert.Equal("前方五十米，左转。",Assert.Single(Update(p,[M(49)])));
        Assert.Empty(Update(p,[M(10)]));
    }

    [Fact]
    public void FarContinuationTransitionsOnceToSpecificOrdinalBriefingAt800()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("沿当前道路，继续行驶一点五公里。",Assert.Single(Update(p,[M(1500)])));
        Assert.Equal("八百米后，在第三个路口左转。",Assert.Single(Update(p,
            [M(100,"s1",ManeuverKind.Straight),M(300,"s2",ManeuverKind.Straight),M(800)])));
        Assert.Empty(Update(p,[M(100,"s1",ManeuverKind.Straight),M(250,"s2",ManeuverKind.Straight),M(500)]));
        Assert.Equal("二百米后，在第二个路口左转。",Assert.Single(Update(p,
            [M(100,"s2",ManeuverKind.Straight),M(200)])));
    }

    [Fact]
    public void StandardAndCatchUpRemindersRetainStillRelevantIntersectionOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(100,"s1",ManeuverKind.Straight),M(200,"s2",ManeuverKind.Straight),M(500)]);
        Assert.Equal("二百米后，在第二个路口左转。",Assert.Single(Update(p,
            [M(50,"s2",ManeuverKind.Straight),M(200)])));
        var other=new NavigationInstructionPolicy();
        Update(other,[M(100,"s1",ManeuverKind.Straight),M(500)]);
        Assert.Equal("一百五十米后，在第二个路口左转。",Assert.Single(Update(other,
            [M(20,"s1",ManeuverKind.Straight),M(175)])));
    }
    [Theory]
    [InlineData(275,"二百五十米后，在下一个路口左转。",true)]
    [InlineData(175,"一百五十米后，在下一个路口左转。",false)]
    [InlineData(79,"五十米后，在下一个路口左转。",false)]
    public void FirstBetweenStagesRoundsAndSkipsFollowingLowerReminder(double distance,string text,bool later50)
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal(text,Assert.Single(Update(p,[M(distance)])));
        Assert.Empty(Update(p,[M(Math.Min(distance,200))]));
        if(later50)Assert.Equal("前方五十米，左转。",Assert.Single(Update(p,[M(50)])));
        else Assert.Empty(Update(p,[M(50)]));
    }

    [Fact]
    public void FirstCloseStraightDoesNotHideTheSecondIntersectionTurn()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("在第二个路口左转。",Assert.Single(Update(p,
            [M(10,"straight",ManeuverKind.Straight),M(35)])));
    }

    [Fact]
    public void FirstActionUnder50UsesReadyImmediateWithoutDistance()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("即将左转。",Assert.Single(Update(p,[M(30)])));
        Assert.Empty(Update(p,[M(10)]));
    }

    [Fact]
    public void ActualActionCompletionImmediatelyBriefsNextActionWithoutStraightCooldown()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(30)]);
        Assert.Equal("六百米后，在下一个路口右转。",Assert.Single(Update(p,
            [M(600,"next",ManeuverKind.TurnRight)],seconds:1)));
    }

    [Fact]
    public void NotReadyDoesNotConsumeFirstBriefingOrItsReminderStages()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Update(p,[M(700)],_=>false));
        Assert.Equal("六百五十米后，在下一个路口左转。",Assert.Single(Update(p,[M(675)])));
        Assert.Empty(Update(p,[M(500)]));
        Assert.Equal("前方二百米，左转。",Assert.Single(Update(p,[M(200)])));
    }

    [Fact]
    public void NotReadyReminderRemainsPendingButCannotPlayAnObsoleteDistance()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(2500)]);
        Assert.Empty(Update(p,[M(2000)],_=>false));
        Assert.Equal("前方一千七百五十米，左转。",Assert.Single(Update(p,[M(1750)])));
        Assert.Empty(Update(p,[M(1000)]));
        Assert.Single(Update(p,[M(500)]));
    }

    [Fact]
    public void PlanIsReadOnlyPreheatsTwoBinsAndNextActionAfterCurrentOne()
    {
        var p=new NavigationInstructionPolicy();
        var plan=Plan(p,[M(275),M(800,"next",ManeuverKind.TurnRight)]);
        Assert.Contains("二百五十米后，在下一个路口左转。",plan);
        Assert.Contains("二百米后，在下一个路口左转。",plan);
        Assert.Contains("一百五十米后，在下一个路口左转。",plan);
        Assert.Contains("五百米后，在下一个路口右转。",plan);
        Assert.Equal("二百五十米后，在下一个路口左转。",Assert.Single(Update(p,[M(275)])));
        Assert.Empty(Update(p,[M(200)]));
    }

    [Fact]
    public void PlanningCannotOverwritePreviousActualUpdateForCrossings()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(2500)]);
        Update(p,[M(2001)]);
        Plan(p,[M(1995)]);
        Assert.Equal("前方二千米，左转。",Assert.Single(Update(p,[M(1995)])));
    }

    [Fact]
    public void NearStraightIsSingleCueAndCannotImmediatelyCrowdActionBriefing()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(45,"straight",ManeuverKind.Straight),M(600)]);
        Assert.Empty(Update(p,[M(40,"straight",ManeuverKind.Straight),M(590)],seconds:1));
        Assert.Equal("保持直行。",Assert.Single(Update(p,[M(30,"straight",ManeuverKind.Straight),M(580)],seconds:6)));
        Assert.Empty(Update(p,[M(20,"straight",ManeuverKind.Straight),M(570)],seconds:7));
    }

    [Fact]
    public void StraightOnlyUsesUnmeasuredContinuationAndLocalPassageCue()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("沿当前道路继续行驶。",Assert.Single(Update(p,[M(100,"s",ManeuverKind.Straight)])));
        Assert.Empty(Update(p,[M(70,"s",ManeuverKind.Straight)],seconds:1));
        Assert.Equal("保持直行。",Assert.Single(Update(p,[M(40,"s",ManeuverKind.Straight)],seconds:6)));
    }

    [Fact]
    public void EmptyAndTemporaryMissingGeometryCannotEraseDeduplication()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(500)]);
        Assert.Empty(Update(p,[]));
        Assert.Empty(Update(p,[M(490)]));
        Assert.Equal("前方二百米，左转。",Assert.Single(Update(p,[M(200)])));
    }

    [Theory]
    [InlineData(false,0)]
    [InlineData(true,3)]
    [InlineData(true,-1)]
    public void InvalidSamplesCannotRegisterBriefingOrConsume(bool active,int age)
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(p.PlanSequence([M(100)],"session",Now.AddSeconds(-age),Now,active));
        Assert.Empty(p.UpdateSequence([M(100)],"session",Now.AddSeconds(-age),Now,active));
        Assert.Single(Update(p,[M(500)]));
    }

    [Fact]
    public void OldObservationsCannotResetOrRebrief()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(30)]);
        Assert.Empty(p.UpdateSequence([M(700)],"session",Now.AddSeconds(-1),Now,true));
        Assert.Empty(Update(p,[M(500)]));
    }

    [Fact]
    public void SessionAndNearThenFarEncounterCanRearm()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(30)]);
        Assert.Empty(Update(p,[M(550)]));
        Assert.Single(Update(p,[M(551)]));
        Assert.Single(p.UpdateSequence([M(500)],"next-session",Now,Now,true));
    }

    [Fact]
    public void InvalidSequenceCannotInventAnOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Update(p,[M(500),M(100,"second")]));
        Assert.Empty(Update(p,[M(double.NaN)]));
        Assert.Empty(Update(p,[M(10),M(20)]));
    }

    [Fact]
    public void FiniteVocabularyDoesNotEnumerateDynamicDistancesOrOrdinals()
    {
        var text=NavigationPrompts.AllTexts().ToArray();
        Assert.Equal(78,text.Length);
        Assert.Equal(78,text.Distinct().Count());
        Assert.Contains("前方二千米，向右前方行驶。",text);
        Assert.Contains("保持直行。",text);
        Assert.Equal("八百米后，在下一个路口右转。",NavigationPrompts.FirstLayer(ManeuverKind.TurnRight,800,1));
        Assert.Equal("沿当前道路，继续行驶八百五十米。",NavigationPrompts.FirstLayer(ManeuverKind.TurnRight,850,1));
        Assert.Equal("一千零一",NavigationPrompts.Number(1001));
    }
}
