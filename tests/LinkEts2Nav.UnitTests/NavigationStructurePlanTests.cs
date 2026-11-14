using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationStructurePlanTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (NearbyMapSnapshot Scene,RouteSnapshot Route) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,new(0,0,0),new(0,0,700),default);
        var right=Road.Add(map,new(-30,0,730),new(-600,0,730),default);
        var straight=Road.Add(map,new(0,0,760),new(0,0,1300),default);
        DirectedRoad[] links=[new("prefab:ABC:0:1",incoming.ForwardNode.Uid,right.Node.Uid,
            [new(0,0,700),new(0,0,710),new(-10,0,725),new(-30,0,730)])
            {Junction=new(2,2,1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=-Vector3.UnitX},
            new("prefab:ABC:0:2",incoming.ForwardNode.Uid,straight.Node.Uid,[new(0,0,700),new(0,0,760)])
            {Junction=new(2,2,1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ}];
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,1300,60),new(incoming.ForwardNode.Uid,600,30),
            new(right.Node.Uid,570,28),new(right.ForwardNode.Uid,0,0)],Now);
        return (new(map,[],[],[]){PrefabPaths=links},route);
    }
    private static GameDataSnapshot Data(RouteSnapshot route,float z,DateTimeOffset now)
        =>new(new(0,0,z,.5,15,1000,true,false,100,now),route,RouteAvailability.Available,"session",true);

    [Fact]
    public void MovementAndSdkObjectRefreshOnlyAdvanceExistingPlans()
    {
        var scene=Scene();var coordinator=new GuidanceCoordinator();var runtime=new NavigationRuntime(guidance:coordinator);
        runtime.Process(Data(scene.Route,10,Now),scene.Scene,Now);
        var original=coordinator.Timeline.Sources.Single(p=>p.Source=="navigation").Sequences;
        for(int i=1;i<30;i++)
        {
            var now=Now.AddMilliseconds(i*200);
            var route=scene.Route with{ReceivedAt=now,Revision="irrelevant test revision "+i,
                Nodes=scene.Route.Nodes.Select(n=>n with{DistanceToEndMeters=n.DistanceToEndMeters*2,TimeToEndSeconds=n.TimeToEndSeconds+3}).ToArray()};
            var data=Data(route,10+i*3,now);
            // A new wrapper and new equivalent annotation dictionary are not map changes.
            runtime.Process(data,scene.Scene with{RoadStructures=new Dictionary<ulong,RoundaboutStructure>()},now);
            coordinator.Tick(data,now);
            var current=coordinator.Timeline.Sources.Single(p=>p.Source=="navigation");
            Assert.Same(original,current.Sequences);Assert.Equal(now,current.ObservedAt);
        }
        Assert.Equal(1,runtime.StructureBuildCount);Assert.Equal(1,runtime.EventPlanBuildCount);
    }

    [Fact]
    public void MapGeometryCompletionAndTrueRouteMeaningRebuildTheStructure()
    {
        var scene=Scene();var coordinator=new GuidanceCoordinator();var runtime=new NavigationRuntime(guidance:coordinator);
        var partial=scene.Scene with{PrefabPaths=[]};
        runtime.Process(Data(scene.Route,10,Now),partial,Now);
        Assert.Equal(1,runtime.StructureBuildCount);
        runtime.Process(Data(scene.Route,20,Now),scene.Scene,Now);
        Assert.Equal(2,runtime.StructureBuildCount);
        Assert.NotNull(runtime.Process(Data(scene.Route,30,Now),scene.Scene,Now).Next);
        Assert.Equal(2,runtime.StructureBuildCount);
        runtime.Process(Data(scene.Route with{MayBeTruncated=true},40,Now),scene.Scene,Now);
        Assert.Equal(3,runtime.StructureBuildCount);
        runtime.Process(Data(scene.Route with{Nodes=scene.Route.Nodes.Select(n=>n with{DistanceToEndMeters=n.DistanceToEndMeters+100}).ToArray()},50,Now),scene.Scene,Now);
        Assert.Equal(4,runtime.StructureBuildCount);
    }

    [Fact]
    public void PassingRoadAndPrefabBoundariesDoesNotRebuildTheGraphOrEventTable()
    {
        var scene=Scene();var coordinator=new GuidanceCoordinator();var runtime=new NavigationRuntime(guidance:coordinator);
        var poses=new[]{(0f,600f,.5),(0f,695f,.5),(-30f,730f,.25),(-100f,730f,.25),(-300f,730f,.25)};
        IReadOnlyList<SpeechSequence>? sequences=null;
        for(int i=0;i<poses.Length;i++)
        {
            var (x,z,heading)=poses[i];var now=Now.AddMilliseconds(i*200);
            var data=Data(scene.Route,z,now) with{Vehicle=new(x,0,z,heading,15,1000,true,false,100,now)};
            runtime.Process(data,scene.Scene,now);coordinator.Tick(data,now);
            var current=coordinator.Timeline.Sources.Single(p=>p.Source=="navigation").Sequences;
            sequences??=current;Assert.Same(sequences,current);
        }
        Assert.Equal(1,runtime.StructureBuildCount);Assert.Equal(1,runtime.EventPlanBuildCount);
    }

    [Fact]
    public void RoadPathComponentsSurvivePositionAndDistanceRefreshButNotTopologyChanges()
    {
        var scene=Scene();var source=new RoadPathSource(scene.Scene.Map,scene.Scene.PrefabPaths);
        var before=source.GetRouteComponents(scene.Route);
        var changedDistances=scene.Route with{Nodes=scene.Route.Nodes.Select(n=>n with{DistanceToEndMeters=n.DistanceToEndMeters*2}).ToArray()};
        Assert.Same(before,source.GetRouteComponents(changedDistances));
        Assert.Same(before[0],source.GetPath(new(0,0,25),Vector3.UnitZ,changedDistances,RouteAvailability.Available));
        Assert.Equal(1,source.RouteStructureBuildCount);
        var trimmed=scene.Route with{Nodes=scene.Route.Nodes.Skip(1).ToArray()};
        Assert.NotSame(before,source.GetRouteComponents(trimmed));Assert.Equal(2,source.RouteStructureBuildCount);
    }

    [Fact]
    public void TailContinuationIsPlannedBeforeTheLastActionHasFinished()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        NavigationManeuver action=new("turn",ManeuverKind.TurnLeft,300,"in","out"){EndDistanceMeters=340};
        var plan=planner.Build("session","route",Now,0,[action],true,false,true,scheduler,"frame",confirmedContinuationEndAlong:2000);
        var tail=Assert.Single(plan.Sequences,s=>s.Id.StartsWith("continuation:"));
        Assert.Equal(340,Assert.Single(tail.Triggers).AlongMeters);
        timeline.Publish(plan);scheduler.Update(timeline,Now,10);
        timeline.UpdateProgress("navigation","session",341,Now.AddSeconds(1),true);
        Assert.Contains(scheduler.Update(timeline,Now.AddSeconds(1),10),r=>r.Trigger.Text==NavigationPrompts.Continue()&&r.Disposition==TriggerDisposition.Queued);
    }

    [Fact]
    public void ExplicitSegmentBreakPreservesMileageAndPositionProjection()
    {
        DirectedRoad collapsed=new("ring",1,3,[new(0,0,0),new(0,0,10),new(0,0,15),new(0,0,25)]){SegmentStarts=[2]};
        Assert.Equal(20,RouteGeometry.Length(collapsed));
        Assert.Equal(15,new DirectedPathMatcher().ProjectDistance(new(0,0,20),Vector3.UnitZ,[collapsed]));
    }

    [Fact]
    public void StructureRefinementReanchorsTheExecutionWindowWithoutRepeatingConsumedPreviews()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        NavigationManeuver M(double distance)=>new("turn",ManeuverKind.TurnLeft,distance,"in","out"){EndDistanceMeters=distance+38.879315};
        timeline.Publish(planner.Build("session","route",Now,0,[M(1000)],true,false,false,scheduler,"frame"));
        scheduler.Update(timeline,Now,10); // The initial overview is already consumed.
        const double distance=26.026826753338355,drift=60.58180618286133;
        double player=1000-drift-distance;
        var refined=planner.Build("session","route",Now,player,[M(distance)],true,false,false,scheduler,"frame");
        timeline.Publish(refined);
        Assert.Equal(player+distance,Assert.Single(refined.Events).AlongMeters,6);
        Assert.Equal(player+distance+38.879315,Assert.Single(refined.Sequences).EndAlongMeters!.Value,6);
        var due=scheduler.Update(timeline,Now,13.75);
        Assert.Contains(due,r=>r.Trigger.Kind==SpeechTriggerKind.Execution&&r.Disposition==TriggerDisposition.Queued);
        Assert.DoesNotContain(due,r=>r.Trigger.Kind==SpeechTriggerKind.Overview);
    }

    [Fact]
    public void RepeatedPhysicalJunctionsRetainIndependentVisitSeriesAcrossPrefixTrimming()
    {
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();
        NavigationManeuver M(double distance)=>new("same-junction",ManeuverKind.TurnLeft,distance,"in","out"){EndDistanceMeters=distance+30};
        var initial=planner.Build("session","route",Now,0,[M(100),M(500)],true,false,false,scheduler,"frame");
        Assert.Equal(2,initial.Sequences.Count);
        var second=initial.Events.MaxBy(e=>e.AlongMeters)!;
        var trimmed=planner.Build("session","trimmed",Now,200,[M(300)],true,false,false,scheduler,"frame");
        Assert.Contains(trimmed.Events,e=>e.Id==second.Id&&e.AlongMeters==500);
    }
}
