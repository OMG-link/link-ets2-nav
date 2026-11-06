using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class WarningPlanningPipelineTests
{
    private sealed class Recording : IWarningAnnouncer, IWarningPlanner
    {
        public List<string> Events { get; } = [];
        public List<CameraWarning> Warnings { get; } = [];
        public IReadOnlyList<PlannedCameraWarning> Plans { get; private set; } = [];
        public void Announce(CameraWarning warning) { Events.Add("trigger"); Warnings.Add(warning); }
        public void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now)
        { Events.Add("plan"); Plans = plans; }
        public void ClearPlan() { Events.Add("clear"); Plans = []; }
    }

    [Fact]
    public void RealRoadPipelinePlansBeforeThresholdsAndEmitsBothStages()
    {
        var now = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        var map = new TruckLib.ScsMap.Map();
        var road = Road.Add(map, Vector3.Zero, new(1200, 0, 0), default);
        var route = RouteSnapshot.Parse([new(road.Node.Uid, 1200, 60), new(road.ForwardNode.Uid, 0, 0)], now);
        var camera = new CameraObservation("camera", new(1000, 0, 0), new(1000, 0, 0),
            Vector3.UnitX, true, "/model/test.pmd", "speedcam.20");
        var nearby = new NearbyMapSnapshot(map, [camera], [], []);
        var output = new Recording();
        var pipeline = new WarningPipeline(output, output);
        GameDataSnapshot Data(double x) => new(new(x, 0, 0, .75, 20, 1200-x, true, false, 1, now),
            route, RouteAvailability.Available, "session", true);
        pipeline.Process(Data(100), nearby, now);
        Assert.Equal([500d, 200d], output.Plans.Select(p => p.StageDistanceMeters));
        Assert.Empty(output.Warnings);
        pipeline.Process(Data(501), nearby, now);
        pipeline.Process(Data(801), nearby, now);
        Assert.Equal([500d, 200d], output.Warnings.Select(w => w.StageDistanceMeters));
        Assert.Equal(["plan", "plan", "trigger", "plan", "trigger"], output.Events);
        pipeline.Process(Data(810) with { IsVehicleUsable = false }, nearby, now);
        Assert.Empty(output.Plans);
        Assert.Equal("clear", output.Events[^1]);
        pipeline.Reset();
        Assert.Equal("clear", output.Events[^1]);
    }
}
