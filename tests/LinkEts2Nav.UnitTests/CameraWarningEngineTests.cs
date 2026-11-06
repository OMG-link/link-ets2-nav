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
        Assert.Empty(engine.Update(Snapshot(Camera(direct: 100, road: 450)), Now));
        var warning = Assert.Single(engine.Update(Snapshot(Camera(direct: 100, road: 300)), Now));
        Assert.Equal(300, warning.DistanceMeters);
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
    public void CrossingThresholdBetweenSamplesIsNotMissed()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(Camera(310)), Now));
        Assert.Single(engine.Update(Snapshot(Camera(290)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(280)), Now));
    }

    [Fact]
    public void FirstObservationInsideThresholdWarns()
    {
        Assert.Single(new CameraWarningEngine().Update(Snapshot(Camera(50)), Now));
    }

    [Fact]
    public void RouteRecalculationAndMissingCandidateDoNotRepeatAnnouncement()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Empty(engine.Update(new("session", "new-route", true, true, Now, []), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(200), revision: "new-route"), Now));
    }

    [Fact]
    public void ReturningAfterPhysicalSeparationStartsNewEncounter()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(10, approach: CameraApproach.Behind)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(300)), Now));
        Assert.Empty(engine.Update(Snapshot(Camera(600, approach: CameraApproach.Behind)), Now));
        Assert.Single(engine.Update(Snapshot(Camera(300)), Now));
    }

    [Fact]
    public void PausedSampleDoesNotRearmOrChangeSession()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Camera()), Now));
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
    public void StaleOrFutureDataCannotTrigger(double ageSeconds)
    {
        var snapshot = Snapshot(Camera()) with { ObservedAt = Now - TimeSpan.FromSeconds(ageSeconds) };
        Assert.Empty(new CameraWarningEngine().Update(snapshot, Now));
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
    [InlineData(300, 300)]
    [InlineData(double.NaN, 600)]
    [InlineData(300, double.PositiveInfinity)]
    public void InvalidThresholdConfigurationFailsEarly(double trigger, double rearm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraWarningEngine(trigger, rearm));
    }
}
