using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class WarningPlanningPipelineTests
{
    private sealed class Recording : IWarningPlanner,IWarningAnnouncer
    {
        public List<string> Events { get; } = [];
        public void Announce(CameraWarning warning)=>Events.Add("unexpected-playback");
        public IReadOnlyList<PlannedCameraWarning> Plans { get; private set; } = [];
        public void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now)
        { Events.Add("plan"); Plans = plans; }
        public void ClearPlan() { Events.Add("clear"); Plans = []; }
    }

    [Fact]
    public void StandaloneCameraAnalysisPreparesButNeverCallsAnAudioCapablePlanner()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,Vector3.Zero,new(1200,0,0),default);
        var route=RouteSnapshot.Parse([new(road.Node.Uid,1200,60),new(road.ForwardNode.Uid,0,0)],now);
        var camera=new CameraObservation("camera",new(1000,0,0),new(1000,0,0),Vector3.UnitX,true,"test","test");
        var recorder=new Recording();var pipeline=new WarningPipeline(recorder);
        var data=new GameDataSnapshot(new(600,0,0,.75,20,600,true,false,1,now),route,RouteAvailability.Available,"s",true);
        var result=pipeline.Process(data,new(map,[camera],[],[]),now);
        Assert.NotEmpty(recorder.Plans);Assert.Equal(new[]{"plan"},recorder.Events);
        Assert.NotNull(result.LastWarning);
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
        var guidance=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();guidance.Queued+=queued.Add;
        var pipeline = new WarningPipeline(guidance:guidance);
        GameDataSnapshot Data(double x) => new(new(x, 0, 0, .75, 20, 1200-x, true, false, 1, now),
            route, RouteAvailability.Available, "session", true);
        void Step(GameDataSnapshot data){guidance.Observe(data,now);pipeline.Process(data,nearby,now);guidance.Tick(data,now);}
        Step(Data(100));
        Assert.Equal(new double[]{-500,-200},guidance.Timeline.Sources[0].Triggers.Select(t=>t.SequenceOrder));
        Assert.Empty(queued);
        Step(Data(501));Step(Data(801));
        Assert.Equal(new double[]{-500,-200},queued.Select(q=>q.SequenceOrder));
        Assert.All(guidance.Diagnostic.LatestTriggers,r=>Assert.NotNull(r.Sequence.End));
        Step(Data(810) with{IsVehicleUsable=false});
        Assert.False(guidance.Timeline.Sources[0].Active);
        pipeline.Reset();Assert.False(guidance.Timeline.Sources[0].Active);
    }
}
