using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class CameraWarningEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static WarningSnapshot Snapshot(CameraCandidate candidate, bool route = true,
        bool active = true, string session = "session", string revision = "route") =>
        new(session, revision, route, active, Now, [candidate]);
    private static CameraCandidate Camera(double direct = 250, double? road = null,
        CameraApproach approach = CameraApproach.RouteAhead, string id = "camera") =>
        new(id, approach, direct, road);

    [Fact]
    public void RoadDistanceTakesPriorityOverNearbyModel()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(direct: 100, road: 550)), Now));
        var warning = Assert.Single(engine.Update(Snapshot(Camera(direct: 100, road: 500)), Now));
        Assert.Equal(500, warning.DistanceMeters);
        Assert.Equal(500, warning.StageDistanceMeters);
        Assert.Equal(WarningDistanceKind.Road, warning.DistanceKind);
    }

    [Fact]
    public void FallsBackToDirectDistanceOnlyWhenRoadDistanceUnavailable()
    {
        var warning = Assert.Single(new CameraWarningEngine().Update(Snapshot(Camera()), Now));
        Assert.Equal(WarningDistanceKind.Euclidean, warning.DistanceKind);
    }

    [Theory]
    [InlineData(CameraApproach.Unknown)]
    [InlineData(CameraApproach.Behind)]
    [InlineData(CameraApproach.UnavoidableAhead)]
    public void ActiveRouteRejectsUnconfirmedRouteMembership(CameraApproach approach)
    {
        Assert.Empty(new CameraWarningEngine().Update(Snapshot(Camera(approach: approach)), Now));
    }

    [Theory]
    [InlineData(CameraApproach.Unknown)]
    [InlineData(CameraApproach.Behind)]
    [InlineData(CameraApproach.RouteAhead)]
    public void NoRouteRejectsAnythingOtherThanCertainForwardContinuation(CameraApproach approach)
    {
        Assert.Empty(new CameraWarningEngine().Update(Snapshot(Camera(approach: approach), route: false), Now));
    }

    [Fact]
    public void NoRouteAllowsConfirmedUnavoidableCamera()
    {
        Assert.Single(new CameraWarningEngine().Update(
            Snapshot(Camera(approach: CameraApproach.UnavoidableAhead), route: false), Now));
    }

    [Fact]
    public void BothExactBoundariesWarnIndependentlyWithoutRepeating()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(500.001)), Now));
        Assert.Equal(500, Assert.Single(engine.Update(Snapshot(Camera(500)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(499)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(200.001)), Now));
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(200)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(199)), Now));
    }

    [Fact]
    public void CrossingThresholdBetweenSamplesIsNotMissed()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(510)), Now));
        Assert.Equal(500, Assert.Single(engine.Update(Snapshot(Camera(490)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(280)), Now));
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(190)), Now)).StageDistanceMeters);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    public void FirstObservationInsideFinalThresholdOnlyWarnsForFinalStage(double distance)
    {
        var engine = new CameraWarningEngine();
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(distance)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(250)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(180)), Now));
    }

    [Fact]
    public void JumpAcrossBothStagesDoesNotQueueObsoleteFarWarning()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(550)), Now));
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(150)), Now)).StageDistanceMeters);
        Assert.Empty(engine.GetPlans(Snapshot(Camera(350)), Now));
    }

    [Fact]
    public void RouteRecalculationAndMissingCandidateDoNotRepeatAnnouncement()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Empty(engine.Update(new("session", "new-route", true, true, Now, []), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(220), revision: "new-route"), Now));
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(200), revision: "new-route"), Now)).StageDistanceMeters);
    }

    [Fact]
    public void ReturningAfterPhysicalSeparationStartsNewEncounter()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(10, approach: CameraApproach.Behind)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(49)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(50, approach: CameraApproach.Behind)), Now));
        Assert.Equal(500, Assert.Single(engine.Update(Snapshot(Camera(300)), Now)).StageDistanceMeters);
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(100)), Now)).StageDistanceMeters);
    }

    [Fact]
    public void ConfirmedPassExpiresUntriggeredNearStageDespiteProjectionJitter()
    {
        var engine = new CameraWarningEngine();
        Assert.Equal(500, Assert.Single(engine.Update(Snapshot(Camera(450)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(10, approach: CameraApproach.Behind)), Now));
        Assert.Empty(engine.GetPlans(Snapshot(Camera(20)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(20)), Now));
    }

    [Fact]
    public void UnknownMatchDoesNotConsumeOrRearmPendingNearStage()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera(450)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(180, approach: CameraApproach.Unknown)), Now));
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(170)), Now)).StageDistanceMeters);
    }

    [Fact]
    public void PlansInterleaveCameraStagesByTriggerTravelDistanceAndDoNotConsumeThem()
    {
        var engine = new CameraWarningEngine();
        var snapshot = Snapshot(Camera()) with { Candidates = [Camera(650, id: "b"), Camera(550, id: "a")] };
        var plans = engine.GetPlans(snapshot, Now);
        Assert.Equal([("a", 500d), ("b", 500d), ("a", 200d), ("b", 200d)], plans.Select(p => (p.CameraId, p.StageDistanceMeters)));
        Assert.Equal([50d, 150d, 350d, 450d], plans.Select(p => p.DistanceUntilTriggerMeters));
        Assert.Equal(plans, engine.GetPlans(snapshot, Now));
        Assert.Equal(500, Assert.Single(engine.Update(Snapshot(Camera(500, id: "a")), Now)).StageDistanceMeters);
        Assert.Equal(200, Assert.Single(engine.GetPlans(Snapshot(Camera(400, id: "a")), Now)).StageDistanceMeters);
    }

    [Fact]
    public void PlanningLateObservationOmitsExpiredStageButDoesNotConsumeFinalStage()
    {
        var engine = new CameraWarningEngine();
        var plan = Assert.Single(engine.GetPlans(Snapshot(Camera(150)), Now));
        Assert.Equal(200, plan.StageDistanceMeters);
        Assert.Equal(0, plan.DistanceUntilTriggerMeters);
        Assert.Equal(200, Assert.Single(engine.Update(Snapshot(Camera(150)), Now)).StageDistanceMeters);
    }

    [Fact]
    public void PausedSampleDoesNotRearmOrChangeSession()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Empty(engine.GetPlans(Snapshot(Camera(1000), active: false, session: "pause"), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(1000), active: false, session: "pause"), Now));
        Assert.Empty(engine.Update(Snapshot(Camera()), Now));
    }

    [Fact]
    public void PausedFirstSampleDoesNotConsumeWarning()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(), active: false), Now));
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
    }

    [Fact]
    public void TeleportOrLoadSessionChangeResetsEncounter()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Single(engine.Update(Snapshot(Camera(), session: "loaded"), Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2.001)]
    [InlineData(60)]
    public void StaleOrFutureDataCannotTriggerOrPlan(double ageSeconds)
    {
        var snapshot = Snapshot(Camera()) with { ObservedAt = Now - TimeSpan.FromSeconds(ageSeconds) };
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.GetPlans(snapshot, Now));
        Assert.Empty(engine.Update(snapshot, Now));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidSuppliedRoadDistanceCannotFallBackToDirectDistance(double distance)
    {
        Assert.Empty(new CameraWarningEngine().Update(Snapshot(Camera(road: distance)), Now));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidDirectPositionDistanceCannotTrigger(double distance)
    {
        Assert.Empty(new CameraWarningEngine().Update(Snapshot(Camera(direct: distance, road: 250)), Now));
    }

    [Fact]
    public void DuplicateCandidateOnlyWarnsOnceAndOtherCameraStillWarns()
    {
        var snapshot = Snapshot(Camera()) with { Candidates = [Camera(), Camera(), Camera(id: "other")] };
        var warnings = new CameraWarningEngine().Update(snapshot, Now);
        Assert.Equal(2, warnings.Count);
        Assert.Equal(["camera", "other"], warnings.Select(warning => warning.CameraId));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(300, 20)]
    [InlineData(double.NaN, 600)]
    [InlineData(300, double.PositiveInfinity)]
    public void InvalidThresholdConfigurationFailsEarly(double trigger, double rearm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraWarningEngine(trigger, rearm));
    }

    [Fact]
    public void ShortCustomThresholdDoesNotInventAnOuterStage()
    {
        var engine = new CameraWarningEngine(100, 600);
        Assert.Equal(100, Assert.Single(engine.GetPlans(Snapshot(Camera(150)), Now)).StageDistanceMeters);
        Assert.Empty(engine.Update(Snapshot(Camera(101)), Now));
        Assert.Equal(100, Assert.Single(engine.Update(Snapshot(Camera(100)), Now)).StageDistanceMeters);
    }
}
