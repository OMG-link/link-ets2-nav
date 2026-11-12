using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class WarningPipelineTests
{
    private sealed class NeverPlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>throw new InvalidOperationException("No fallback audio is configured.");}
    [Fact]
    public async Task NoNavigationCameraEndsOnlyAfterCrossingItsRealMonitoringPoint()
    {
        var map=new GameMap();AddRoad(map,Point(0),Point(800));
        var nearby=Nearby(map,Camera());var empty=RouteSnapshot.Parse([],Now);
        await using var playback=new GuidancePlaybackManager(null,new NeverPlayer(),()=>null);
        var guidance=new GuidanceCoordinator(playback:playback);
        var pipeline=new WarningPipeline(guidance:guidance);
        void Step(double x)
        {
            var data=Data(empty,Vehicle(x));guidance.Observe(data,Now);
            pipeline.Process(data,nearby,Now);guidance.Tick(data,Now);
        }
        Step(50);Assert.Single(playback.Pending);
        Step(348);Assert.Single(playback.Pending);
        Step(349);Assert.Single(playback.Pending);
        Step(350);Assert.Empty(playback.Pending);
        Assert.Contains(guidance.Diagnostic.LatestTriggers,t=>t.Disposition==LinkEts2Nav.Core.Guidance.TriggerDisposition.Ended);
    }
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

    [Fact]
    public void VerifiedForwardCameraOnActualNavigationRoadWarnsOnlyOnce()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        var pipeline = announcer;
        var nearby = Nearby(map, Camera());
        var data = Data(Route(road));
        pipeline.Process(data, nearby, Now);
        pipeline.Process(data, nearby, Now);
        var warning = Assert.Single(announcer.Queued);
        Assert.StartsWith("landmark:camera:", warning.EventId);
        Assert.Equal(WarningDistanceKind.Road,Assert.Single(announcer.Guidance.Timeline.Sources.Single(s=>s.Source=="landmarks").Events).DistanceKind);
        Assert.InRange(Assert.Single(announcer.Latest!.Cameras).RoadDistanceMeters??Assert.Single(announcer.Latest.Cameras).DistanceMeters, 298.99, 299.01);
    }

    [Fact]
    public void CandidateOutsideThresholdDoesNotWarnUntilEnteringRange()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(800));
        var announcer = new RecordedWarningPipeline();
        var pipeline = announcer;
        var nearby = Nearby(map, Camera(position: Point(549)));
        var route = Route(road);
        pipeline.Process(Data(route, Vehicle(x: 40)), nearby, Now);
        Assert.Empty(announcer.Queued);
        pipeline.Process(Data(route, Vehicle(x: 50)), nearby, Now);
        Assert.Single(announcer.Queued);
    }

    [Fact]
    public void InactiveLocatorNeverProducesAudioEvenWhenItLooksForward()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        var diagnostic = announcer.Process(Data(Route(road)),
            Nearby(map, Camera(verified: false)), Now);
        Assert.Empty(announcer.Queued);
        Assert.False(Assert.Single(diagnostic.Cameras).DirectionVerified);
    }

    [Fact]
    public void VerifiedButUnknownDirectionCannotWarn()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(Route(road)), Nearby(map, Camera(direction: Vector3.Zero)), Now);
        Assert.Empty(announcer.Queued);
    }

    [Fact]
    public void OppositeFacingMonitoringDirectionDoesNotWarn()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(Route(road)), Nearby(map, Camera(direction: -Vector3.UnitX)), Now);
        Assert.Empty(announcer.Queued);
    }

    [Fact]
    public void ExplicitNoRouteAllowsSameRoadUnavoidableCamera()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(500));
        var noRoute = RouteSnapshot.Parse([], Now);
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(noRoute), Nearby(map, Camera()), Now);
        Assert.Single(announcer.Queued);
    }

    [Fact]
    public void NearbyCameraOnParallelNonRouteRoadCannotProduceAudio()
    {
        var map = new GameMap();
        var routeRoad = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(Route(routeRoad)),
            Nearby(map, Camera(position: Point(349, 8))), Now);
        Assert.Empty(announcer.Queued);
    }

    [Fact]
    public void StaleVehicleSampleCannotTriggerEvenIfCallerMarkedItUsable()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(Route(road), Vehicle(received: Now.AddSeconds(-3))),
            Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Queued);
    }

    [Fact]
    public void PausedStoreSnapshotCannotTrigger()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var store = new GameSnapshotStore();
        store.UpdateVehicle(Vehicle() with { Paused = true });
        store.UpdateRoute(Route(road));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(store.Capture(Now), Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Queued);
    }

    [Theory]
    [InlineData(RouteAvailability.Unknown)]
    [InlineData(RouteAvailability.Invalid)]
    [InlineData(RouteAvailability.Stale)]
    public void UnavailableRouteCannotBecomeNoRouteFallback(RouteAvailability availability)
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        var data = Data(Route(road)) with { RouteAvailability = availability };
        announcer.Process(data, Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Queued);
    }

    [Fact]
    public void NewSectorMapRebuildsRoadGeometryWithoutRepeatingSameEncounter()
    {
        var firstMap = new GameMap();
        var firstRoad = AddRoad(firstMap, Point(0), Point(500));
        var secondMap = new GameMap();
        var secondRoad = AddRoad(secondMap, Point(0), Point(800));
        var announcer = new RecordedWarningPipeline();
        var pipeline = announcer;
        pipeline.Process(Data(Route(firstRoad)), Nearby(firstMap, Camera()), Now);
        var cameras=Nearby(secondMap,Camera(),Camera(id:"new-camera",position:Point(700)));
        pipeline.Process(Data(Route(secondRoad)), cameras, Now);
        Assert.Single(announcer.Queued); // The later same-kind preview waits for the first camera's end.
        pipeline.Process(Data(Route(secondRoad),Vehicle(350)),cameras,Now);
        Assert.Equal(["camera", "new-camera"], announcer.Queued.Select(warning => warning.EventId.Split(':')[1]));
    }

    [Fact]
    public void NewGameSessionRearmsPreviouslyWarnedCamera()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var announcer = new RecordedWarningPipeline();
        var pipeline = announcer;
        var route = Route(road);
        var nearby = Nearby(map, Camera());
        pipeline.Process(Data(route), nearby, Now);
        pipeline.Process(Data(route, session: "loaded-session"), nearby, Now);
        Assert.Equal(2, announcer.Queued.Count);
    }
    [Fact]
    public void VehicleOnParallelNonRouteRoadCannotBorrowNearbyRouteGeometry()
    {
        var map = new GameMap();
        var routeRoad = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(Route(routeRoad), Vehicle() with { Z = 8 }),
            Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Queued);
    }    [Fact]
    public void VerifiedFutureRouteChunkUsesDirectDistanceWhenPrefabGeometryIsUnknown()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = RouteSnapshot.Parse([new(first.Node.Uid, 500, 25), new(first.ForwardNode.Uid, 300, 15),
            new(second.Node.Uid, 250, 12), new(second.ForwardNode.Uid, 0, 0)], Now);
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(route), Nearby(map, Camera()), Now);
        var warning = Assert.Single(announcer.Queued);
        Assert.Equal(WarningDistanceKind.Euclidean,Assert.Single(announcer.Guidance.Diagnostic.LatestTriggers).Event.DistanceKind);
        Assert.InRange(Assert.Single(announcer.Latest!.Cameras).RoadDistanceMeters??Assert.Single(announcer.Latest.Cameras).DistanceMeters, 298.99, 299.01);
    }

    [Fact]
    public void NoRouteUniquelyConnectedRoadChainUsesRoadDistance()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        first.Append(Point(500));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(RouteSnapshot.Parse([], Now)), Nearby(map, Camera()), Now);
        Assert.Single(announcer.Queued);
        Assert.Equal(WarningDistanceKind.Road,Assert.Single(announcer.Guidance.Diagnostic.LatestTriggers).Event.DistanceKind);
    }

    [Fact]
    public void NoRouteCannotUseDirectDistanceAcrossUnconnectedGap()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(200));
        AddRoad(map, Point(250), Point(500));
        var announcer = new RecordedWarningPipeline();
        announcer.Process(Data(RouteSnapshot.Parse([], Now)), Nearby(map, Camera()), Now);
        Assert.Empty(announcer.Queued);
    }}
