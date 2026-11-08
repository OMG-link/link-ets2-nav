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

    [Theory]
    [InlineData(999, "沿当前道路，继续行驶九百五十米。")]
    [InlineData(1250, "沿当前道路，继续行驶一点二公里。")]
    [InlineData(2650, "沿当前道路，继续行驶二点六公里。")]
    [InlineData(9876, "沿当前道路，继续行驶九点八公里。")]
    [InlineData(10000, "沿当前道路，继续行驶十公里。")]
    [InlineData(10849, "沿当前道路，继续行驶十公里。")]
    [InlineData(12345, "沿当前道路，继续行驶十二公里。")]
    public void LongDistancesUseCoarserRounding(double distance,string expected)
        => Assert.Equal(expected,NavigationPrompts.Continue(distance));

    [Theory]
    [InlineData(50,"五十米后，左转。")]
    [InlineData(950,"九百五十米后，左转。")]
    [InlineData(1000,"一公里后，左转。")]
    [InlineData(1500,"一点五公里后，左转。")]
    [InlineData(1750,"一点七五公里后，左转。")]
    [InlineData(2000,"两公里后，左转。")]
    public void ManeuverDistanceUsesAfterAndSwitchesToKilometers(int meters,string expected)
    {
        Assert.Equal(expected,NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,meters));
        Assert.Equal(expected,LinkEts2Nav.Speech.SpeechPack.NavigationDistance(meters)+"后，左转。");
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(1000)]
    [InlineData(500)]
    [InlineData(200)]
    public void TwentyMeterFollowupUsesBareActionAfterActualAnnouncement(int earlierDistance)
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(earlierDistance)]);
        Update(p,[M(200)]);
        Update(p,[M(51)]);
        Assert.Contains("左转。",Plan(p,[M(51)]));
        Assert.DoesNotContain("五十米后，左转。",Plan(p,[M(51)]));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
        Assert.Empty(Update(p,[M(10)]));
    }

    [Fact]
    public void PlanningAndUnreadyAudioDoNotRemoveFirstFiftyMeterPrefix()
    {
        var p=new NavigationInstructionPolicy();
        Plan(p,[M(500)]);
        Assert.Empty(Update(p,[M(500)],_=>false));
        Assert.Empty(Update(p,[M(200)],_=>false));
        Assert.Equal("五十米后，在下一个路口左转。",Assert.Single(Update(p,[M(50)])));
    }

    [Fact]
    public void PreviousActionDoesNotRemovePrefixForNewActionsFirstFiftyMeterCue()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(30)]);
        Assert.Equal("五十米后，在下一个路口右转。",Assert.Single(Update(p,[M(50,"next",ManeuverKind.TurnRight)])));
    }

    [Fact]
    public void TwentyMeterFollowupUsesBareActionAndDropsOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(200)]);
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
        var q=new NavigationInstructionPolicy();
        Update(q,[M(100,"straight",ManeuverKind.Straight),M(500)]);
        Assert.Equal("左转。",Assert.Single(Update(q,[M(10,"straight",ManeuverKind.Straight),M(20)])));
    }

    [Theory]
    [InlineData(200,"两百米后，左转。")]
    [InlineData(250,"两百五十米后，左转。")]
    [InlineData(2000,"两公里后，左转。")]
    public void IntegerDistanceUsesLiang(int meters,string expected)
    {
        Assert.Equal(expected,NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,meters));
        Assert.Equal(expected,LinkEts2Nav.Speech.SpeechPack.NavigationDistance(meters)+"后，左转。");
    }

    [Theory]
    [InlineData(1200,"一点二公里")]
    [InlineData(2100,"二点一公里")]
    [InlineData(2200,"二点二公里")]
    public void DecimalDistanceRetainsEr(int meters,string expected)
    {
        Assert.Equal("沿当前道路，继续行驶"+expected+"。",NavigationPrompts.Continue(meters));
        Assert.Equal(expected,LinkEts2Nav.Speech.SpeechPack.NavigationDistance(meters));
    }

    [Fact]
    public void PendingHighwayOverviewDoesNotChaseEveryChangingDistance()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Update(p,[M(2650),M(6000,"later")],_=>false));
        var plan=Plan(p,[M(2550),M(5900,"later")]);
        Assert.Equal("沿当前道路，继续行驶二点六公里。",plan[0]);
        Assert.Single(plan,t=>t.StartsWith("沿当前道路"));
        Assert.Equal(plan[0],Assert.Single(Update(p,[M(2550),M(5900,"later")])));
        Assert.DoesNotContain(Plan(p,[M(2450),M(5800,"later")]),t=>t.StartsWith("沿当前道路"));
        Assert.Empty(Update(p,[M(2450),M(5800,"later")]));
    }

    [Fact]
    public void OldPendingHighwayOverviewCannotReplaceAnImminentAction()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(2500)],_=>false);
        Assert.Equal("七百五十米后，在下一个路口左转。",Assert.Single(Update(p,[M(750)])));
    }

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
        Assert.Equal("两公里后，左转。",Assert.Single(Update(p,[M(1995)])));
        Assert.Empty(Update(p,[M(1001)]));
        Assert.Equal("一公里后，左转。",Assert.Single(Update(p,[M(995)])));
        Assert.Empty(Update(p,[M(800)]));
        Assert.Empty(Update(p,[M(750)]));
        Assert.Empty(Update(p,[M(550)]));
        Assert.Empty(Update(p,[M(501)]));
        Assert.Equal("五百米后，左转。",Assert.Single(Update(p,[M(498)])));
        Assert.Empty(Update(p,[M(201)]));
        Assert.Equal("两百米后，左转。",Assert.Single(Update(p,[M(198)])));
        Assert.Empty(Update(p,[M(101)]));
        Assert.Equal("一百米后，左转。",Assert.Single(Update(p,[M(99)])));
        Assert.Empty(Update(p,[M(25)]));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
        Assert.Empty(Update(p,[M(10)]));
    }

    [Fact]
    public void FarContinuationKeepsOnlyStandardRemindersAndTheirOrdinals()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(1500)]);
        Assert.Equal("一公里后，左转。",Assert.Single(Update(p,[M(1000)])));
        Assert.Empty(Update(p,[M(100,"s1",ManeuverKind.Straight),M(300,"s2",ManeuverKind.Straight),M(800)]));
        Assert.Equal("五百米后，在第三个路口左转。",Assert.Single(Update(p,
            [M(100,"s1",ManeuverKind.Straight),M(250,"s2",ManeuverKind.Straight),M(500)])));
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(Update(p,
            [M(100,"s2",ManeuverKind.Straight),M(200)])));
    }

    [Fact]
    public void HighwayOverviewNeverSchedulesAnAdditional800MeterBriefing()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal(NavigationPrompts.Continue(1400),Assert.Single(Update(p,[M(1400)])));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,1000),Assert.Single(Update(p,[M(1000)])));
        foreach(double distance in new[]{900d,800,775,750,700,600,550,501})
        {
            var plan=Plan(p,[M(distance)]);
            Assert.Equal(new[]{NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,500),
                NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,200),
                NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,100),
                NavigationPrompts.Execute(ManeuverKind.TurnLeft)},plan);
            Assert.Empty(Update(p,[M(distance)]));
        }
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,500),Assert.Single(Update(p,[M(499)])));
    }

    [Fact]
    public void GenericOverviewAndNewlyDiscoveredNearActionDoNotBothBriefTheSameLeg()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true);
        Assert.DoesNotContain(Plan(p,[M(750)]),t=>t.Contains("七百五十")||t.Contains("六百五十"));
        Assert.Empty(Update(p,[M(750)]));
        Assert.Empty(Update(p,[M(550)]));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,500),Assert.Single(Update(p,[M(500)])));
    }

    [Fact]
    public void StandardAndCatchUpRemindersRetainStillRelevantIntersectionOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(100,"s1",ManeuverKind.Straight),M(200,"s2",ManeuverKind.Straight),M(500)]);
        Assert.Equal("两百米后，在第二个路口左转。",Assert.Single(Update(p,
            [M(50,"s2",ManeuverKind.Straight),M(200)])));
        var other=new NavigationInstructionPolicy();
        Update(other,[M(100,"s1",ManeuverKind.Straight),M(500)]);
        Assert.Empty(Update(other,[M(20,"s1",ManeuverKind.Straight),M(175)]));
        Assert.Equal("一百米后，在第二个路口左转。",Assert.Single(Update(other,
            [M(10,"s1",ManeuverKind.Straight),M(100)])));
    }
    [Theory]
    [InlineData(275,"两百五十米后，在下一个路口左转。",true)]
    [InlineData(175,"一百五十米后，在下一个路口左转。",false)]
    [InlineData(79,"五十米后，在下一个路口左转。",false)]
    public void FirstBetweenStagesRoundsAndSkipsFollowingLowerReminder(double distance,string text,bool later100)
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal(text,Assert.Single(Update(p,[M(distance)])));
        Assert.Empty(Update(p,[M(Math.Min(distance,200))]));
        if(later100)Assert.Equal("一百米后，左转。",Assert.Single(Update(p,[M(100)])));
        else Assert.Empty(Update(p,[M(100)]));
        Assert.Empty(Update(p,[M(50)]));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
    }

    [Fact]
    public void FirstCloseStraightDoesNotHideTheSecondIntersectionTurn()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Equal("保持直行。",Assert.Single(Update(p,
            [M(10,"straight",ManeuverKind.Straight),M(35)])));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
    }

    [Fact]
    public void FirstActionUnder50UsesReadyImmediateWithoutDistance()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Update(p,[M(30)]));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(10)])));
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
        Assert.Equal("两百米后，左转。",Assert.Single(Update(p,[M(200)])));
    }

    [Fact]
    public void NotReadyReminderRemainsPendingButCannotPlayAnObsoleteDistance()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(2500)]);
        Assert.Empty(Update(p,[M(2000)],_=>false));
        Assert.Empty(Update(p,[M(1750)]));
        Assert.Equal("一公里后，左转。",Assert.Single(Update(p,[M(1000)])));
        Assert.Single(Update(p,[M(500)]));
    }

    [Fact]
    public void PlanIsReadOnlyPreheatsTwoBinsAndNextActionAfterCurrentOne()
    {
        var p=new NavigationInstructionPolicy();
        var plan=Plan(p,[M(275),M(800,"next",ManeuverKind.TurnRight)]);
        Assert.Contains("两百五十米后，在下一个路口左转。",plan);
        Assert.Contains("两百米后，在下一个路口左转。",plan);
        Assert.Contains("一百五十米后，在下一个路口左转。",plan);
        Assert.Contains("五百米后，在下一个路口右转。",plan);
        Assert.Equal("两百五十米后，在下一个路口左转。",Assert.Single(Update(p,[M(275)])));
        Assert.Empty(Update(p,[M(200)]));
    }

    [Fact]
    public void PlanningCannotOverwritePreviousActualUpdateForCrossings()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(2500)]);
        Update(p,[M(2001)]);
        Plan(p,[M(1995)]);
        Assert.Equal("两公里后，左转。",Assert.Single(Update(p,[M(1995)])));
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
        Assert.Empty(Update(p,[M(100,"s",ManeuverKind.Straight)]));
        Assert.DoesNotContain(NavigationPrompts.Continue(),p.PlanSequence([M(100,"s",ManeuverKind.Straight)],"session",Now,Now,true));
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
        Assert.Equal("两百米后，左转。",Assert.Single(Update(p,[M(200)])));
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
        Update(p,[M(10)]);
        Assert.Empty(p.UpdateSequence([M(700)],"session",Now.AddSeconds(-1),Now,true));
        Assert.Empty(Update(p,[M(500)]));
    }

    [Fact]
    public void SessionAndNearThenFarEncounterCanRearm()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(10)]);
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
        Assert.Equal(112,text.Length);
        Assert.Equal(112,text.Distinct().Count());
        Assert.Contains("两公里后，向右前方行驶。",text);
        Assert.Contains("保持直行。",text);
        Assert.Equal("八百米后，在下一个路口右转。",NavigationPrompts.FirstLayer(ManeuverKind.TurnRight,800,1));
        Assert.Equal("沿当前道路，继续行驶八百五十米。",NavigationPrompts.FirstLayer(ManeuverKind.TurnRight,850,1));
        Assert.Equal("一千零一",NavigationPrompts.Number(1001));
    }
    [Fact]
    public void ConfirmedEmptyCorridorSpeaksOnceButUnknownEmptyDoesNot()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Plan(p,[]));
        Assert.Empty(Update(p,[]));
        Assert.Contains(NavigationPrompts.Continue(),p.PlanSequence([],"session",Now,Now,true,true));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,_=>false,true));
        Assert.Equal(NavigationPrompts.Continue(),Assert.Single(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true)));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,false,hasConfirmedContinuation:true));
        Assert.Empty(Update(p,[]));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true));
    }

    [Fact]
    public void GenericContinuationCoversFarOverviewButAllowsSpecificUpcomingGuidance()
    {
        var p=new NavigationInstructionPolicy();
        p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true);
        Assert.Empty(Update(p,[M(2500)]));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,2000),Assert.Single(Update(p,[M(2000)])));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,1000),Assert.Single(Update(p,[M(1000)])));
        Assert.Empty(Update(p,[M(800)]));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,500),Assert.Single(Update(p,[M(500)])));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true));
    }

    [Fact]
    public void DistanceOverviewSuppressesGenericUntilTargetActuallyPassed()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(1500)]);
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true));
        Update(p,[M(10)]);
        Assert.Equal(NavigationPrompts.Continue(),Assert.Single(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true)));
        Assert.Empty(p.UpdateSequence([],"session",Now,Now,true,hasConfirmedContinuation:true));
        Assert.Empty(Update(p,[M(1500,"next")]));
    }

    [Fact]
    public void NewPassedTargetLegMayAnnounceAnotherFarOverview()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(1500)]);
        Update(p,[M(10)]);
        Assert.Equal(NavigationPrompts.Continue(1500),Assert.Single(Update(p,[M(1500,"next")])));
    }

    [Fact]
    public void PreparedBoundaryCueCanCompleteUnreadyCloseBriefingExactlyOnce()
    {
        var p=new NavigationInstructionPolicy();
        string fixedCue=NavigationPrompts.AtDistance(ManeuverKind.TurnRight,500);
        bool Ready(string text)=>text==fixedCue;
        Assert.Empty(Update(p,[M(501,kind:ManeuverKind.TurnRight)],Ready));
        Assert.Equal(fixedCue,Assert.Single(Update(p,[M(499,kind:ManeuverKind.TurnRight)],Ready)));
        Assert.Empty(Update(p,[M(498,kind:ManeuverKind.TurnRight)],Ready));
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnRight,200),Assert.Single(Update(p,[M(200,kind:ManeuverKind.TurnRight)])));
    }

    [Fact]
    public void HighwayOverviewLeavesItsNextStandardBoundaryAvailable()
    {
        var p=new NavigationInstructionPolicy();
        Update(p,[M(900,kind:ManeuverKind.TurnRight)]);
        string fixedCue=NavigationPrompts.AtDistance(ManeuverKind.TurnRight,500);
        bool Ready(string text)=>text==fixedCue;
        Assert.Empty(Update(p,[M(501,kind:ManeuverKind.TurnRight)],Ready));
        Assert.Equal(fixedCue,Assert.Single(Update(p,[M(499,kind:ManeuverKind.TurnRight)],Ready)));
        Assert.Empty(Update(p,[M(498,kind:ManeuverKind.TurnRight)],Ready));
    }

    [Fact]
    public void PreparedBoundaryCannotDropIntersectionOrdinalOrInventCrossing()
    {
        var p=new NavigationInstructionPolicy();
        string fixedCue=NavigationPrompts.AtDistance(ManeuverKind.TurnRight,500);
        bool Ready(string text)=>text==fixedCue;
        Assert.Empty(Update(p,[M(100,"straight",ManeuverKind.Straight),M(501,kind:ManeuverKind.TurnRight)],Ready));
        Assert.Empty(Update(p,[M(90,"straight",ManeuverKind.Straight),M(499,kind:ManeuverKind.TurnRight)],Ready));
        var q=new NavigationInstructionPolicy();
        Assert.Empty(Update(q,[M(650,kind:ManeuverKind.TurnRight)],Ready));
        Assert.Empty(Update(q,[M(600,kind:ManeuverKind.TurnRight)],Ready));
    }
    [Fact]
    public void MainRoadContinuationDoesNotProduceLocalStraightCueButCountsInOrdinal()
    {
        var p=new NavigationInstructionPolicy();
        NavigationManeuver Main(double distance)=>M(distance,"main",ManeuverKind.Straight) with { IsMainRoadContinuation=true };
        Assert.Equal(NavigationPrompts.FirstLayer(ManeuverKind.TurnLeft,600,2),Assert.Single(Update(p,[Main(45),M(600)])));
        Assert.DoesNotContain(NavigationPrompts.Immediate(ManeuverKind.Straight),Plan(p,[Main(40),M(590)]));
        Assert.Empty(Update(p,[Main(30),M(580)],seconds:6));
    }
    [Fact]
    public void UnknownOrdinalWithPrecedingStraightCannotUseFixedBoundaryFallback()
    {
        var p=new NavigationInstructionPolicy();
        string fixedCue=NavigationPrompts.AtDistance(ManeuverKind.TurnRight,500);
        bool Ready(string text)=>text==fixedCue;
        NavigationManeuver Target(double distance)=>M(distance,kind:ManeuverKind.TurnRight) with { HasSeparateApproachRoad=false };
        Assert.Empty(Update(p,[M(100,"straight",ManeuverKind.Straight),Target(550)],Ready));
        Assert.Empty(Update(p,[M(90,"straight",ManeuverKind.Straight),Target(499)],Ready));
    }
}
