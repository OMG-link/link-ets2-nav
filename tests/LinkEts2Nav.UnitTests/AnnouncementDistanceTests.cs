using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class AnnouncementDistanceTests
{
    private static readonly DateTimeOffset Now = new(2026,11,7,6,0,0,TimeSpan.Zero);
    private static WarningSnapshot Snapshot(double distance, CameraApproach approach = CameraApproach.RouteAhead,
        bool route = true, string session = "session") => new(session,"route",route,true,Now,
            [new CameraCandidate("camera", approach, distance, distance)]);

    [Theory]
    [InlineData(200)]
    [InlineData(199.7)]
    [InlineData(150)]
    public void PreviouslyConfirmedTargetUses200AtEvenWhenSamplingCrossesBoundary(double distance)
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(600), Now));
        Assert.Single(engine.Update(Snapshot(498), Now));
        var warning = Assert.Single(engine.Update(Snapshot(distance), Now));
        Assert.Equal(200, warning.StageDistanceMeters);
        Assert.False(warning.FirstConfirmedInside200Meters);
        Assert.Equal("二百米处，有测速照相。", PlannedSpeechAnnouncer.Text(
            warning.StageDistanceMeters, warning.DistanceMeters, warning.FirstConfirmedInside200Meters));
    }

    [Theory]
    [InlineData(199.7)]
    [InlineData(150)]
    public void FirstConfirmedInsideBoundaryUsesWithinInBothPlanAndWarning(double distance)
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(600, CameraApproach.Unknown, false), Now));
        var snapshot = Snapshot(distance, CameraApproach.UnavoidableAhead, false);
        var plan = Assert.Single(engine.GetPlans(snapshot, Now));
        Assert.True(plan.FirstConfirmedInside200Meters);
        Assert.Equal(200, plan.StageDistanceMeters);
        // GetPlans is called by both planner and Update; the first observation must survive both.
        var warning = Assert.Single(engine.Update(snapshot, Now));
        Assert.True(warning.FirstConfirmedInside200Meters);
        Assert.Equal("二百米内，有测速照相。", PlannedSpeechAnnouncer.Text(
            warning.StageDistanceMeters, warning.DistanceMeters, warning.FirstConfirmedInside200Meters));
        Assert.Empty(engine.Update(snapshot, Now));
    }

    [Fact]
    public void Exactly200OnFirstObservationUsesAt()
    {
        var warning = Assert.Single(new CameraWarningEngine().Update(Snapshot(200), Now));
        Assert.False(warning.FirstConfirmedInside200Meters);
        Assert.Equal("二百米处，有测速照相。", PlannedSpeechAnnouncer.Text(200,200,warning.FirstConfirmedInside200Meters));
    }

    [Fact]
    public void BriefUnknownOrRouteChangeDoesNotForgetEarlierConfirmation()
    {
        var engine = new CameraWarningEngine();
        Assert.Empty(engine.Update(Snapshot(300) with { IsActive = false }, Now));
        Assert.Single(engine.Update(Snapshot(300), Now));
        Assert.Empty(engine.Update(Snapshot(250,CameraApproach.Unknown), Now));
        var warning = Assert.Single(engine.Update(Snapshot(198) with { RouteRevision = "changed" }, Now));
        Assert.False(warning.FirstConfirmedInside200Meters);
    }

    [Fact]
    public void SessionResetAndPassingThenLeaving50MetersStartFreshEncounters()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(500), Now));
        var fresh = Assert.Single(engine.Update(Snapshot(150,session:"new"), Now));
        Assert.True(fresh.FirstConfirmedInside200Meters);
        engine.Update(Snapshot(10,CameraApproach.Behind,session:"new"), Now);
        engine.Update(Snapshot(55,CameraApproach.Behind,session:"new"), Now);
        // The next approach is confirmed early; a previous late discovery must not carry over.
        Assert.Empty(engine.Update(Snapshot(600,session:"new"), Now));
        var next = Assert.Single(engine.Update(Snapshot(199,session:"new"), Now));
        Assert.False(next.FirstConfirmedInside200Meters);
    }
}
