using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationInstructionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static NavigationManeuver M(double distance, string id = "junction", ManeuverKind kind = ManeuverKind.TurnLeft)
        => new(id, kind, distance, "entry", "exit");
    private static IReadOnlyList<string> Plan(NavigationInstructionPolicy policy, double distance)
        => policy.Plan(M(distance), "session", Now, Now, true);
    private static IReadOnlyList<string> Update(NavigationInstructionPolicy policy, double distance)
        => policy.Update(M(distance), "session", Now, Now, true);

    [Fact]
    public void NormalApproachSpeaks500200And50Once()
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Contains("前方五百米，左转。", Plan(policy, 700));
        Assert.Empty(Update(policy, 700));
        Assert.Equal("前方五百米，左转。", Assert.Single(Update(policy, 500)));
        Assert.Empty(Update(policy, 499));
        Assert.Equal("前方二百米，左转。", Assert.Single(Update(policy, 200)));
        Assert.Empty(Update(policy, 199));
        Assert.Equal("前方五十米，左转。", Assert.Single(Update(policy, 50)));
        Assert.Empty(Update(policy, 20));
        Assert.Empty(Plan(policy, 0));
    }

    [Theory]
    [InlineData(275, "前方二百五十米，左转。", true)]
    [InlineData(499, "前方四百五十米，左转。", true)]
    [InlineData(250, "前方二百五十米，左转。", true)]
    [InlineData(175, "前方一百五十米，左转。", false)]
    [InlineData(100, "前方一百米，左转。", false)]
    [InlineData(79, "前方五十米，左转。", false)]
    [InlineData(75, "前方五十米，左转。", false)]
    public void FirstCatchUpFloorsTo50AndSkipsNextLowerStage(double distance, string expected, bool later50)
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Equal(expected, Plan(policy, distance)[0]);
        Assert.Equal(expected, Assert.Single(Update(policy, distance)));
        Assert.Empty(Update(policy, Math.Min(distance, 200)));
        if (later50) Assert.Equal("前方五十米，左转。", Assert.Single(Update(policy, 50)));
        else Assert.Empty(Update(policy, 50));
        Assert.Empty(Update(policy, 10));
    }

    [Fact]
    public void StraightFirstSeenAt79OnlyReports50MetersOnce()
    {
        var policy = new NavigationInstructionPolicy();
        var first = M(79, kind: ManeuverKind.Straight);
        Assert.Equal("前方五十米，直行。", Assert.Single(policy.Update(first,"session",Now,Now,true)));
        Assert.Empty(policy.Update(first with { DistanceMeters=50 },"session",Now,Now,true));
        Assert.Empty(policy.Update(first with { DistanceMeters=20 },"session",Now,Now,true));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(200)]
    [InlineData(50)]
    public void ExactInitialStandardStagePreservesFollowingStages(int distance)
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Equal(NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,distance),Assert.Single(Update(policy,distance)));
        if(distance>200) Assert.Equal("前方二百米，左转。",Assert.Single(Update(policy,200)));
        if(distance>50) Assert.Equal("前方五十米，左转。",Assert.Single(Update(policy,50)));
        Assert.Empty(Update(policy,20));
    }

    [Theory]
    [InlineData(ManeuverKind.TurnLeft,"即将左转。")]
    [InlineData(ManeuverKind.TurnRight,"即将右转。")]
    [InlineData(ManeuverKind.KeepLeft,"即将靠左行驶。")]
    [InlineData(ManeuverKind.KeepRight,"即将靠右行驶。")]
    [InlineData(ManeuverKind.Straight,"保持直行。")]
    [InlineData(ManeuverKind.UTurn,"即将掉头。")]
    public void FirstBelow50UsesImmediateTextAndNoLater50(ManeuverKind kind,string expected)
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Equal(expected,Assert.Single(policy.Update(M(49,kind:kind),"session",Now,Now,true)));
        Assert.Empty(policy.Update(M(10,kind:kind),"session",Now,Now,true));
        Assert.Empty(policy.Update(M(50,kind:kind),"session",Now,Now,true));
    }

    [Fact]
    public void PlanCannotRegisterEarlierDistanceOrPretendAThresholdWasCrossed()
    {
        var policy=new NavigationInstructionPolicy();
        Plan(policy,700);
        Plan(policy,501);
        Assert.Equal("前方四百五十米，左转。",Assert.Single(Update(policy,498)));
        Assert.Empty(Update(policy,200));
    }

    [Fact]
    public void PlanDoesNotOverwriteTheLastUpdateUsedForNormalCrossing()
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Empty(Update(policy,501));
        Assert.Equal("前方五百米，左转。",Plan(policy,498)[0]);
        Assert.Equal("前方五百米，左转。",Assert.Single(Update(policy,498)));
        Assert.Empty(Update(policy,201));
        Assert.Equal("前方二百米，左转。",Plan(policy,198)[0]);
        Assert.Equal("前方二百米，左转。",Assert.Single(Update(policy,198)));
        Assert.Empty(Update(policy,51));
        Assert.Equal("前方五十米，左转。",Assert.Single(Update(policy,49)));
    }

    [Theory]
    [InlineData(480,"前方五百米，左转。",true)]
    [InlineData(479.9,"前方四百五十米，左转。",false)]
    public void NormalCrossingToleranceIs20Meters(double distance,string expected,bool later200)
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,501);
        Assert.Equal(expected,Assert.Single(Update(policy,distance)));
        if(later200) Assert.Single(Update(policy,200));
        else Assert.Empty(Update(policy,200));
    }

    [Theory]
    [InlineData(49,"前方五十米，左转。")]
    [InlineData(30,"前方五十米，左转。")]
    [InlineData(29,"即将左转。")]
    public void LastStandardCrossingKeeps50UnlessMoreThan20MetersLate(double distance,string expected)
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);
        Update(policy,200);
        Assert.Empty(Update(policy,51));
        Assert.Equal(expected,Plan(policy,distance)[0]);
        Assert.Equal(expected,Assert.Single(Update(policy,distance)));
    }

    [Fact]
    public void Rounded250CatchUpLeaves50AsTheNextNormalStandardCrossing()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,275);
        Assert.Empty(Update(policy,51));
        Assert.Equal("前方五十米，左转。",Assert.Single(Update(policy,49)));
    }

    [Theory]
    [InlineData(198,"前方一百五十米，左转。")]
    [InlineData(49,"即将左转。")]
    public void SkippingMultipleStandardsCannotMasqueradeAsNormalLowerCrossing(double distance,string expected)
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Empty(Update(policy,600));
        Assert.Equal(expected,Plan(policy,distance)[0]);
        Assert.Equal(expected,Assert.Single(Update(policy,distance)));
        Assert.Empty(Update(policy,50));
    }

    [Theory]
    [InlineData(200,"前方二百米，左转。")]
    [InlineData(50,"前方五十米，左转。")]
    public void ExactLowerStandardRemainsValidAfterSkippingHigherStandards(int distance,string expected)
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,600);
        Assert.Equal(expected,Assert.Single(Update(policy,distance)));
    }
    [Fact]
    public void LargeJumpFrom700To275RoundsAndSkips200()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,700);
        Assert.Equal("前方二百五十米，左转。",Assert.Single(Update(policy,275)));
        Assert.Empty(Update(policy,200));
        Assert.Single(Update(policy,50));
    }

    [Fact]
    public void RestoringAt150After500CatchUpSkips50()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);
        Update(policy,230);
        Assert.Empty(policy.Update(null,"session",Now,Now,true));
        Assert.Equal("前方一百五十米，左转。",Assert.Single(Update(policy,150)));
        Assert.Empty(Update(policy,50));
    }

    [Fact]
    public void CrossingAllStagesAtOnceOnlyReportsImmediateCue()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,700);
        Assert.Equal("即将左转。",Assert.Single(Update(policy,10)));
        Assert.Empty(Update(policy,50));
        Assert.Empty(Update(policy,200));
    }

    [Fact]
    public void MissingManeuverDoesNotEraseConsumedStage()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);
        Assert.Empty(policy.Update(null,"session",Now,Now,true));
        Assert.Empty(policy.Plan(null,"session",Now,Now,true));
        Assert.Empty(Update(policy,490));
        Assert.Single(Update(policy,200));
    }

    [Theory]
    [InlineData(false,0)]
    [InlineData(true,3)]
    [InlineData(true,-1)]
    public void InvalidSamplesDoNotRegisterCrossingsOrConsume(bool active,int age)
    {
        var policy=new NavigationInstructionPolicy();
        var observed=Now.AddSeconds(-age);
        Assert.Empty(policy.Plan(M(501),"session",observed,Now,active));
        Assert.Empty(policy.Update(M(501),"session",observed,Now,active));
        Assert.Equal("前方四百五十米，左转。",Assert.Single(Update(policy,498)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidDistancesDoNotChangeState(double distance)
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Empty(Plan(policy,distance));
        Assert.Empty(Update(policy,distance));
        Assert.Single(Update(policy,500));
    }

    [Fact]
    public void SessionAndExplicitResetAllowInstructionsAgain()
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Single(Update(policy,500));
        Assert.Empty(Update(policy,500));
        Assert.Single(policy.Update(M(500),"next-session",Now,Now,true));
        policy.Reset();
        Assert.Single(policy.Update(M(500),"next-session",Now,Now,true));
    }

    [Fact]
    public void RearmRequiresNearVisitAndStrictlyMoreThan550Meters()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);Update(policy,200);Update(policy,50);
        Assert.Empty(Plan(policy,550));
        Assert.Empty(Update(policy,550));
        Assert.Empty(Plan(policy,50));
        Assert.Contains("前方五百米，左转。",Plan(policy,551));
        Assert.Empty(Update(policy,551));
        Assert.Single(Update(policy,500));
    }

    [Fact]
    public void MovingAwayBeforeNearVisitDoesNotRearm500()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);Update(policy,200);
        Assert.Empty(Update(policy,700));
        Assert.Empty(Update(policy,500));
    }

    [Fact]
    public void OldUpdateCannotResetEncounterOrManufactureCrossing()
    {
        var policy=new NavigationInstructionPolicy();
        Update(policy,500);Update(policy,200);Update(policy,50);
        Assert.Empty(policy.Plan(M(700),"session",Now.AddSeconds(-1),Now,true));
        Assert.Empty(policy.Update(M(700),"session",Now.AddSeconds(-1),Now,true));
        Assert.Empty(Update(policy,500));
    }

    [Fact]
    public void SeparateIdsHaveSeparateState()
    {
        var policy=new NavigationInstructionPolicy();
        Assert.Single(Update(policy,500));
        Assert.Single(policy.Update(M(500,"other"),"session",Now,Now,true));
    }

    [Fact]
    public void VocabularyContains66UniqueSentencesAndRejectsUnsupportedDistances()
    {
        var texts=NavigationPrompts.AllTexts().ToArray();
        Assert.Equal(66,texts.Length);
        Assert.Equal(66,texts.Distinct().Count());
        Assert.Contains("前方二百五十米，靠左行驶。",texts);
        Assert.Contains("前方五百米，直行。",texts);
        Assert.Contains("保持直行。",texts);
        Assert.Contains("即将掉头。",texts);
        Assert.Throws<ArgumentOutOfRangeException>(()=>NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,25));
        Assert.Throws<ArgumentOutOfRangeException>(()=>NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,550));
        Assert.Throws<ArgumentOutOfRangeException>(()=>NavigationPrompts.Action((ManeuverKind)999));
    }
}
