using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class WarningPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static Vector3 Point(float x, float z = 0, float y = 0) => new(x, y, z);
    private static Road AddRoad(GameMap map, Vector3 start, Vector3 end) =>
        Road.Add(map, start, end, default);

    private static CameraObservation Camera(string id = "camera", bool verified = true,
        Vector3? position = null, Vector3? direction = null) =>
        new(id, position ?? Point(349), position ?? Point(349), direction ?? Vector3.UnitX,
            verified, "/model/test_camera.pmd", "speedcam.test");

    private static NearbyMapSnapshot Nearby(GameMap map, params CameraObservation[] cameras) =>
        new(map, cameras, [], []);

    private static VehicleSnapshot Vehicle(double x = 50, DateTimeOffset? received = null) =>
        new(x, 0, 0, .75, 20, 1000, true, false, 100, received ?? Now);

    private static RouteSnapshot Route(Road road) => RouteSnapshot.Parse(
        [new RouteNode(road.Node.Uid, 500, 25), new RouteNode(road.ForwardNode.Uid, 0, 0)], Now);

    private static GameDataSnapshot Data(RouteSnapshot route, VehicleSnapshot? vehicle = null,
        string session = "session") => new(vehicle ?? Vehicle(), route, route.Availability, session, true);

    private sealed class RecordingAnnouncer : IWarningAnnouncer
    {
        public List<CameraWarning> Warnings { get; } = [];
        public void Announce(CameraWarning warning) => Warnings.Add(warning);
    }

    [Fact]
    public void VerifiedForwardCameraOnActualNavigationRoadWarnsOnlyOnce()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        var pipeline = new WarningPipeline(announcer);
        var nearby = Nearby(map, Camera());
        var data = Data(Route(road));
        pipeline.Process(data, nearby, Now);
        pipeline.Process(data, nearby, Now);
        var warning = Assert.Single(announcer.Warnings);
        Assert.Equal("camera", warning.CameraId);
        Assert.Equal(WarningDistanceKind.Road, warning.DistanceKind);
        Assert.InRange(warning.DistanceMeters, 298.99, 299.01);
    }

    [Fact]
    public void CandidateOutsideThresholdDoesNotWarnUntilEnteringRange()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(800));
        var announcer = new RecordingAnnouncer();
        var pipeline = new WarningPipeline(announcer);
        var nearby = Nearby(map, Camera(position: Point(549)));
        var route = Route(road);
        pipeline.Process(Data(route, Vehicle(x: 40)), nearby, Now);
        Assert.Empty(announcer.Warnings);
        pipeline.Process(Data(route, Vehicle(x: 50)), nearby, Now);
        Assert.Single(announcer.Warnings);
    }

    [Fact]
    public void UnverifiedLocatorNeverProducesAudioEvenWhenItLooksForward()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        var diagnostic = new WarningPipeline(announcer).Process(Data(Route(road)),
            Nearby(map, Camera(verified: false)), Now);
        Assert.Empty(announcer.Warnings);
        Assert.False(Assert.Single(diagnostic.Cameras).DirectionVerified);
    }

    [Fact]
    public void VerifiedButUnknownDirectionCannotWarn()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(Route(road)), Nearby(map, Camera(direction: Vector3.Zero)), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Fact]
    public void OppositeFacingMonitoringDirectionDoesNotWarn()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(Route(road)), Nearby(map, Camera(direction: -Vector3.UnitX)), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Fact]
    public void ExplicitNoRouteAllowsSameRoadUnavoidableCamera()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(500));
        var noRoute = RouteSnapshot.Parse([], Now);
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(noRoute), Nearby(map, Camera()), Now);
        Assert.Single(announcer.Warnings);
    }

    [Fact]
    public void NearbyCameraOnParallelNonRouteRoadCannotProduceAudio()
    {
        var map = new GameMap();
        var routeRoad = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(Route(routeRoad)),
            Nearby(map, Camera(position: Point(349, 8))), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Fact]
    public void StaleVehicleSampleCannotTriggerEvenIfCallerMarkedItUsable()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(Route(road), Vehicle(received: Now.AddSeconds(-3))),
            Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Fact]
    public void PausedStoreSnapshotCannotTrigger()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle() with { Paused = true });
        store.UpdateRoute(Route(road));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(store.Capture(Now), Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Theory]
    [InlineData(RouteAvailability.Unknown)]
    [InlineData(RouteAvailability.Invalid)]
    [InlineData(RouteAvailability.Stale)]
    public void UnavailableRouteCannotBecomeNoRouteFallback(RouteAvailability availability)
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        var data = Data(Route(road)) with { RouteAvailability = availability };
        new WarningPipeline(announcer).Process(data, Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Warnings);
    }

    [Fact]
    public void NewSectorMapRebuildsRoadGeometryWithoutRepeatingSameEncounter()
    {
        var firstMap = new GameMap();
        var firstRoad = AddRoad(firstMap, Point(0), Point(500));
        var secondMap = new GameMap();
        var secondRoad = AddRoad(secondMap, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        var pipeline = new WarningPipeline(announcer);
        pipeline.Process(Data(Route(firstRoad)), Nearby(firstMap, Camera()), Now);
        pipeline.Process(Data(Route(secondRoad)), Nearby(secondMap, Camera(), Camera(id: "new-camera")), Now);
        Assert.Equal(["camera", "new-camera"], announcer.Warnings.Select(warning => warning.CameraId));
    }

    [Fact]
    public void NewGameSessionRearmsPreviouslyWarnedCamera()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordingAnnouncer();
        var pipeline = new WarningPipeline(announcer);
        var route = Route(road);
        var nearby = Nearby(map, Camera());
        pipeline.Process(Data(route), nearby, Now);
        pipeline.Process(Data(route, session: "loaded-session"), nearby, Now);
        Assert.Equal(2, announcer.Warnings.Count);
    }
    [Fact]
    public void VehicleOnParallelNonRouteRoadCannotBorrowNearbyRouteGeometry()
    {
        var map = new GameMap();
        var routeRoad = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(Route(routeRoad), Vehicle() with { Z = 8 }),
            Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Warnings);
    }    [Fact]
    public void VerifiedFutureRouteChunkUsesDirectDistanceWhenPrefabGeometryIsUnknown()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = RouteSnapshot.Parse([new(first.Node.Uid, 500, 25), new(first.ForwardNode.Uid, 300, 15),
            new(second.Node.Uid, 250, 12), new(second.ForwardNode.Uid, 0, 0)], Now);
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(route), Nearby(map, Camera()), Now);
        var warning = Assert.Single(announcer.Warnings);
        Assert.Equal(WarningDistanceKind.Euclidean, warning.DistanceKind);
        Assert.InRange(warning.DistanceMeters, 298.99, 299.01);
    }

    [Fact]
    public void NoRouteUniquelyConnectedRoadChainUsesRoadDistance()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        first.Append(Point(500));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(RouteSnapshot.Parse([], Now)), Nearby(map, Camera()), Now);
        Assert.Equal(WarningDistanceKind.Road, Assert.Single(announcer.Warnings).DistanceKind);
    }

    [Fact]
    public void NoRouteCannotUseDirectDistanceAcrossUnconnectedGap()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(200));
        AddRoad(map, Point(250), Point(500));
        var announcer = new RecordingAnnouncer();
        new WarningPipeline(announcer).Process(Data(RouteSnapshot.Parse([], Now)), Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Warnings);
    }}
