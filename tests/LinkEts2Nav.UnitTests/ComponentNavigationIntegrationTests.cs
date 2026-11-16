using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class ComponentNavigationIntegrationTests
{
    private static DirectedRoad[] HardJoin(bool allDie=false)
    {
        NativeLaneCurve Curve(int id,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(id,a,b,b-a,b-a,next,previous);
        var native=new NativeLaneGeometry(42,[Curve(0,new(0,0,allDie?-30:0),new(90,0,0),[2],[]),
            Curve(1,new(0,0,30),new(90,0,0),[2],[]),Curve(2,new(90,0,0),new(100,0,0),[],[0,1])]);
        DirectedRoad Path(int first,ulong input)
        {
            var start=native.Curves[first].Start;Vector3[] points=[start,new(90,0,0),new(100,0,0)];
            var lane=new PrefabLanePath(42,input,30,0,0,0,0,1,1,[first,2],points,points.Zip(points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second)))
            {NativeGeometry=native,CurveStartPointIndices=[0,1],CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
            return new($"prefab:000000000000002A:{input:X16}:000000000000001E",input,30,points)
            {LanePaths=[lane],JoinPriority=new(10,[],[],8,"cached scored main entrance"),
                NativeEntryTangent=native.Curves[first].StartTangent,NativeExitTangent=native.Curves[2].EndTangent};
        }
        return [Path(0,10),Path(1,20)];
    }
    [Fact]
    public void CompleteTwoWayOneToOnePassagesDoNotInventAMissingComponent()
    {
        DirectedRoad Edge(ulong a,ulong b,float start,float finish)
        {
            Vector3[] points=[new(0,0,start),new(0,0,finish)];
            var geometry=new NativeLaneGeometry(42,[new(0,points[0],points[1],points[1]-points[0],points[1]-points[0],[],[])]);
            var lane=new PrefabLanePath(42,a,b,0,0,0,0,1,1,[0],points,100){NativeGeometry=geometry};
            return new($"prefab:2A:{a}:{b}",a,b,points){LanePaths=[lane]};
        }
        var forward=Edge(1,2,0,100);var reverse=Edge(2,1,100,0);var detector=new NavigationManeuverDetector();
        Assert.Empty(detector.FindSequence([forward],0,[forward,reverse],out _,out bool unknown,out _));
        Assert.False(unknown);Assert.Empty(detector.Components.Issues);
    }
    [Fact]
    public void SnapshotNativeGraphAndComponentFactsAreSharedWithoutLegacyAuxiliaryIndex()
    {
        var graph=new NativeLaneGraph(HardJoin());
        var components=ComponentContinuityIndex.Build(graph);
        var snapshot=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[])
            {PrefabPaths=graph.Edges,NativeGraph=graph,Components=components};
        Assert.Null(snapshot.AuxiliaryLanes);
        var paths=new RoadPathSource(snapshot.Map,snapshot.PrefabPaths,native:snapshot.NativeGraph);
        Assert.Same(graph.Edges,paths.GetNavigationGraph());
        var planner=new ComponentManeuverPlanner();planner.UseIndex(graph.Edges,components);
        var selected=graph.Edges[1];var actions=planner.Build([selected],paths.GetNavigationGraph());
        Assert.Equal(ManeuverKind.KeepRight,Assert.Single(actions[0]).Kind);
        Assert.Empty(planner.Issues);
    }
    [Fact]
    public void ProductionDetectorConsumesHardComponentWithoutCrossingOrBlinkerFallback()
    {
        var graph=HardJoin();var detector=new NavigationManeuverDetector();
        var actions=detector.FindSequence([graph[1]],0,graph);
        var action=Assert.Single(actions);
        Assert.StartsWith("component:",action.Id);Assert.Equal(ManeuverKind.KeepRight,action.Kind);
        Assert.Equal("向右前方行驶，注意左侧来车",action.ExecutionInstruction);
        Assert.Equal(RouteGeometry.Length(graph[1]),action.EndDistanceMeters);
    }
    [Fact]
    public void MainEntranceIdentityDoesNotSilenceARealLocalLaneDeath()
    {
        var graph=HardJoin(true);
        var facts=ComponentContinuityIndex.Build(new(graph)).ForEntry(graph[0].Id)!;
        Assert.True(facts.IsMain);Assert.Equal(ComponentContinuityKind.Hard,facts.Kind);
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([graph[0]],0,graph));
        Assert.NotEqual(ManeuverKind.Straight,action.Kind);Assert.False(action.IsMainRoadContinuation);
    }
    [Fact]
    public void UninterruptedOneToOnePrefabDoesNotCreateAFakeJunction()
    {
        var edge=HardJoin()[0];var lane=edge.LanePaths[0] with{CurveIndices=[0],NativeGeometry=new(42,[new(0,Vector3.Zero,new(100,0,0),new(100,0,0),new(100,0,0),[],[])])};
        edge=edge with{LanePaths=[lane],JoinPriority=null};
        Assert.Empty(new NavigationManeuverDetector().FindSequence([edge],0,[edge]));
    }
    [Fact]
    public void StructurePlannerUsesTheSameComponentAuthorityAndTrueLifetime()
    {
        var graph=HardJoin();var route=new[]{graph[1]};var native=new NativeLaneGraph(graph);
        var auxiliary=AuxiliaryLaneIndex.Build(native);var source=new RoadPathSource(new(),graph,native:native);
        var scene=new NearbyMapSnapshot(new(),[],[],[]){PrefabPaths=graph,AuxiliaryLanes=auxiliary};
        var lanes=new LaneNavigationPlan([new(route[0].Id,route[0].LanePaths[0])],[],true,null);
        var sdk=new RouteSnapshot([new(20,100,1),new(30,0,0)],"test",RouteAvailability.Available,DateTimeOffset.UtcNow);
        var plan=new NavigationStructurePlanner().Build(route,sdk,scene,source,lanes,auxiliary);
        Assert.Single(plan.Actions,a=>a.Id.StartsWith("component:")&&a.Kind==ManeuverKind.KeepRight);
        Assert.DoesNotContain(plan.Actions,a=>a.Id.StartsWith("direct-join:")||a.Id.StartsWith("entrance-merge:"));
    }
}
