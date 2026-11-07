using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationInstructionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static NavigationManeuver M(double distance, string id = "junction")
        => new(id, ManeuverKind.TurnLeft, distance, "entry", "exit");
    private static IReadOnlyList<string> Plan(NavigationInstructionPolicy policy, double distance)
        => policy.Plan(M(distance), "session", Now, Now, true);
    private static IReadOnlyList<string> Update(NavigationInstructionPolicy policy, double distance)
        => policy.Update(M(distance), "session", Now, Now, true);

    [Fact]
    public void NormalApproachPlansBothStagesWithoutConsumingAndSpeaksEachOnce()
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Equal(new[] { "前方三百米，左转。", "前方五十米，左转。", "前方左转。" }, Plan(policy, 700));
        Assert.Empty(Update(policy, 301));
        Assert.Equal("前方三百米，左转。", Assert.Single(Update(policy, 300)));
        Assert.Empty(Update(policy, 299));
        Assert.Equal(new[] { "前方五十米，左转。", "前方左转。" }, Plan(policy, 100));
        Assert.Equal("前方五十米，左转。", Assert.Single(Update(policy, 50)));
        Assert.Empty(Update(policy, 20));
        Assert.Empty(Plan(policy, 0));
    }

    [Theory]
    [InlineData(271, "前方三百米，左转。")]
    [InlineData(270, "前方左转。")]
    [InlineData(100, "前方左转。")]
    public void LateAdvanceDiscoveryDoesNotClaim300Meters(double distance, string expected)
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Equal(expected, Plan(policy, distance)[0]);
        Assert.Equal(expected, Assert.Single(Update(policy, distance)));
    }

    [Theory]
    [InlineData(50, "前方五十米，左转。")]
    [InlineData(40, "前方五十米，左转。")]
    [InlineData(39.9, "前方左转。")]
    [InlineData(0, "前方左转。")]
    public void FirstNearConfirmationOmitsObsoleteAdvanceStage(double distance, string expected)
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Equal(expected, Plan(policy, distance)[0]);
        Assert.Equal(expected, Assert.Single(Update(policy, distance)));
        Assert.Empty(Update(policy, distance));
    }

    [Fact]
    public void OneSampleCrossingBothBoundariesOnlySpeaksNearStage()
    {
        var policy = new NavigationInstructionPolicy();
        Plan(policy, 700);
        Assert.Equal("前方左转。", Assert.Single(Update(policy, 35)));
        Assert.Empty(Update(policy, 100));
        Assert.Empty(Update(policy, 300));
    }

    [Fact]
    public void MissingManeuverOrChangingRoutePrefixDoesNotEraseConsumedStage()
    {
        var policy = new NavigationInstructionPolicy();
        Update(policy, 290);
        Assert.Empty(policy.Update(null, "session", Now, Now, true));
        Assert.Empty(policy.Plan(null, "session", Now, Now, true));
        Assert.Empty(Update(policy, 280));
        Assert.Equal("前方五十米，左转。", Assert.Single(Update(policy, 45)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    [InlineData(true, -1)]
    public void PausedStaleOrFutureSamplesDoNotRegisterFirstDistanceOrConsume(bool active, int age)
    {
        var policy = new NavigationInstructionPolicy();
        var observedAt = Now.AddSeconds(-age);
        Assert.Empty(policy.Plan(M(10), "session", observedAt, Now, active));
        Assert.Empty(policy.Update(M(10), "session", observedAt, Now, active));
        Plan(policy, 500);
        Assert.Equal("前方三百米，左转。", Assert.Single(Update(policy, 300)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidDistancesDoNotChangeState(double distance)
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Empty(Plan(policy, distance));
        Assert.Empty(Update(policy, distance));
        Assert.Equal("前方三百米，左转。", Assert.Single(Update(policy, 300)));
    }

    [Fact]
    public void ExplicitResetAndFreshSessionAllowInstructionsAgain()
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Single(Update(policy, 300));
        Assert.Empty(Update(policy, 300));
        Assert.Single(policy.Update(M(300), "another-session", Now, Now, true));
        policy.Reset();
        Assert.Single(policy.Update(M(300), "another-session", Now, Now, true));
    }

    [Fact]
    public void ReapproachNeedsReachedNearStageAndDistanceStrictlyAbove350()
    {
        var policy = new NavigationInstructionPolicy();
        Update(policy, 300);
        Update(policy, 50);
        Assert.Empty(Update(policy, 300));
        Assert.Empty(Plan(policy, 350));
        Assert.Empty(Update(policy, 50));
        Assert.Equal(3, Plan(policy, 351).Count);
        Assert.Equal("前方三百米，左转。", Assert.Single(Update(policy, 300)));
    }

    [Fact]
    public void MovingAwayBeforeReachingNearThresholdDoesNotRearm()
    {
        var policy = new NavigationInstructionPolicy();
        Update(policy, 300);
        Assert.Equal(2, Plan(policy, 700).Count);
        Assert.Empty(Update(policy, 300));
    }

    [Fact]
    public void OlderObservedSamplesCannotResetAnEncounter()
    {
        var policy = new NavigationInstructionPolicy();
        policy.Update(M(40), "session", Now, Now, true);
        Assert.Empty(policy.Plan(M(500), "session", Now.AddSeconds(-1), Now, true));
        Assert.Empty(Update(policy, 300));
    }

    [Fact]
    public void RestoredMatchingAfterSkippingThresholdUsesPreparedGenericSentence()
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Contains("前方左转。", Plan(policy, 700));
        Assert.Equal("前方左转。", Assert.Single(Update(policy, 150)));
        Assert.Contains("前方左转。", Plan(policy, 100));
        Assert.Equal("前方左转。", Assert.Single(Update(policy, 10)));
    }
    [Fact]
    public void SeparateExitIdsHaveSeparateInstructionState()
    {
        var policy = new NavigationInstructionPolicy();
        Assert.Single(Update(policy, 300));
        Assert.Single(policy.Update(M(300, "different-exit"), "session", Now, Now, true));
    }

    [Fact]
    public void AllFixedTextsAreUniqueAndCoverEveryAction()
    {
        var texts = NavigationPrompts.AllTexts().ToArray();
        Assert.Equal(18, texts.Length);
        Assert.Equal(18, texts.Distinct().Count());
        Assert.Contains("前方三百米，靠左行驶。", texts);
        Assert.Contains("前方五十米，直行。", texts);
        Assert.Contains("前方掉头。", texts);
        Assert.Throws<ArgumentOutOfRangeException>(() => NavigationPrompts.Action((ManeuverKind)999));
    }
}
