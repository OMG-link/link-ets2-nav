using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class DetourWarningTests
{
    private static readonly DateTimeOffset Now = new(2026,11,7,7,0,0,TimeSpan.Zero);
    private static WarningSnapshot Snapshot(CameraCandidate candidate)
        => new("session","route",true,true,Now,[candidate]);
    private static CameraCandidate Direct(double distance, CameraApproach approach = CameraApproach.RouteAhead,
        double? road = null, double? bound = null) => new("camera",approach,distance,road)
        { RouteDistanceLowerBoundMeters = bound };

    [Fact]
    public void PhysicallyNearCameraAfterLongRouteDetourDoesNotConsumeWarningsBeforeRealApproach()
    {
        var map = new TruckLib.ScsMap.Map();
        var outgoing = Road.Add(map, new(0,0,0), new(1000,0,0), default);
        var returning = Road.Add(map, new(1000,0,20), new(-400,0,20), default);
        var route = RouteSnapshot.Parse([new(outgoing.Node.Uid,4000,40),
            new(outgoing.ForwardNode.Uid,3000,30),new(returning.Node.Uid,2000,20),
            new(returning.ForwardNode.Uid,0,0)],Now);
        var camera = new MonitoredCamera("camera",new(50,0,20),-Vector3.UnitX);
        var paths = new RoadPathSource(map);
        var engine = new CameraWarningEngine();
        foreach(float x in new[]{-0f,40f,50f,70f,100f})
        {
            var candidate = paths.MatchCamera(new(x,0,0),Vector3.UnitX,camera,route,RouteAvailability.Available);
            Assert.Equal(CameraApproach.RouteAhead,candidate.Approach);
            Assert.Null(candidate.RoadDistanceMeters);
            Assert.True(candidate.RouteDistanceLowerBoundMeters > 1500);
            Assert.Empty(engine.Update(Snapshot(candidate),Now));
        }
        var far = paths.MatchCamera(new(549,0,20),-Vector3.UnitX,camera,route,RouteAvailability.Available);
        Assert.Equal(500,Assert.Single(engine.Update(Snapshot(far),Now)).StageDistanceMeters);
        var near = paths.MatchCamera(new(249,0,20),-Vector3.UnitX,camera,route,RouteAvailability.Available);
        var warning = Assert.Single(engine.Update(Snapshot(near),Now));
        Assert.Equal(200,warning.StageDistanceMeters);
        Assert.False(warning.FirstConfirmedInside200Meters);
    }

    [Fact]
    public void LeavingFiftyMetersDoesNotImmediatelyRepeatButLaterContinuousRoadApproachCanWarn()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Direct(303)),Now));
        Assert.Single(engine.Update(Snapshot(Direct(199)),Now));
        Assert.Empty(engine.Update(Snapshot(Direct(10)),Now));
        foreach(double d in new[]{49d,50d,51d,80d,300d,700d})
            Assert.Empty(engine.Update(Snapshot(Direct(d)),Now));
        Assert.Empty(engine.Update(Snapshot(Direct(1100,road:1153)),Now));
        Assert.Equal(500,Assert.Single(engine.Update(Snapshot(Direct(480,road:499)),Now)).StageDistanceMeters);
        Assert.Equal(200,Assert.Single(engine.Update(Snapshot(Direct(190,road:199)),Now)).StageDistanceMeters);
    }

    [Fact]
    public void DirectFallbackNeedsRenewedApproachAfterDepartureAndToleratesSmallJitter()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Direct(150)),Now));
        engine.Update(Snapshot(Direct(10,CameraApproach.Behind)),Now);
        foreach(double d in new[]{50d,51d,50d,52d,100d,99d,98d})
            Assert.Empty(engine.Update(Snapshot(Direct(d)),Now));
        var returning = Assert.Single(engine.Update(Snapshot(Direct(94)),Now));
        Assert.Equal(200,returning.StageDistanceMeters);
        Assert.True(returning.FirstConfirmedInside200Meters);
    }

    [Fact]
    public void NearbyFutureRouteChunkDoesNotCountAsPhysicalPassage()
    {
        var engine = new CameraWarningEngine();
        Assert.Single(engine.Update(Snapshot(Direct(400)),Now));
        Assert.Empty(engine.Update(Snapshot(Direct(10,bound:1200)),Now));
        Assert.Empty(engine.Update(Snapshot(Direct(50,bound:1000)),Now));
        // We did not pass this future occurrence. The already consumed 500 m stage stays consumed.
        Assert.Empty(engine.Update(Snapshot(Direct(300,road:350)),Now));
        Assert.Equal(200,Assert.Single(engine.Update(Snapshot(Direct(190,road:199)),Now)).StageDistanceMeters);
    }
}
