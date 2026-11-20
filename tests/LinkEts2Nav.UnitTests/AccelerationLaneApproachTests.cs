using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

// Synthetic native topology exercises the current snapshot/structure pipeline, not the retired raw-graph adapter.
public sealed class AccelerationLaneApproachTests
{
    private static (NearbyMapSnapshot Nearby,DirectedRoad[] Route) Scene(double length,bool mirror=false)
    {
        var original=ComponentContinuityTests.Soft(length);
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(100,0,0),new((float)(100+length),0,0),default);
        ulong Uid(ulong uid)=>uid==30?road.Node.Uid:uid==40?road.ForwardNode.Uid:uid;
        Vector3 Mirror(Vector3 p)=>mirror?p with{Z=-p.Z}:p;
        var geometries=original.SelectMany(e=>e.LanePaths).Select(p=>p.NativeGeometry!).DistinctBy(g=>g.PrefabUid)
            .ToDictionary(g=>g.PrefabUid,g=>g with{Curves=g.Curves.Select(c=>c with
            {Start=Mirror(c.Start),End=Mirror(c.End),StartTangent=Mirror(c.StartTangent),EndTangent=Mirror(c.EndTangent)}).ToArray()});
        var graph=original.Select(e=>e with{StartNodeUid=Uid(e.StartNodeUid),EndNodeUid=Uid(e.EndNodeUid),Points=e.Points.Select(Mirror).ToArray(),
            NativeEntryTangent=e.NativeEntryTangent is {} input?Mirror(input):null,NativeExitTangent=e.NativeExitTangent is {} output?Mirror(output):null,
            LanePaths=e.LanePaths.Select(p=>p with{EntryNodeUid=Uid(p.EntryNodeUid),ExitNodeUid=Uid(p.ExitNodeUid),
                Points=p.Points.Select(Mirror).ToArray(),NativeGeometry=geometries[p.PrefabUid]}).ToArray()}).ToArray();
        var terminal=graph[3] with{LanePaths=[..graph[3].LanePaths,..graph[4].LanePaths]};
        var native=new NativeLaneGraph([graph[0],graph[1],graph[2],terminal]);
        return (new(map,[],[],[]){PrefabPaths=native.Edges,NativeGraph=native,Components=ComponentContinuityIndex.Build(native)},
            [graph[1],graph[2],terminal]);
    }
    private static NavigationStructurePlan Plan(NearbyMapSnapshot scene,DirectedRoad[] route)
    {
        using var source=new RoadPathSource(scene.Map,scene.PrefabPaths,native:scene.NativeGraph);
        return new NavigationStructurePlanner().Build(route,new([],"native",RouteAvailability.None,DateTimeOffset.UtcNow),
            scene,source,new LaneRoutePlanner().Build(route),Enumerable.Range(0,route.Length).ToArray());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SoftEntranceUsesNativeDirectionAndLeavesItsTerminalPrefabBeforeEnding(bool mirror)
    {
        var(scene,route)=Scene(50,mirror);var plan=Plan(scene,route);
        var cue=Assert.Single(plan.Actions,a=>a.Id.StartsWith("component:")&&a.ApproachInterval is not null);
        Assert.Equal(mirror?ManeuverKind.KeepRight:ManeuverKind.KeepLeft,cue.Kind);
        Assert.Equal(RouteGeometry.Length(route[0]),cue.DistanceMeters,3);
        Assert.Equal(route.Sum(RouteGeometry.Length),cue.EndDistanceMeters!.Value,3);
        Assert.True(cue.EndDistanceMeters>cue.ApproachInterval!.EndMeters);
        Assert.DoesNotContain(plan.Actions,a=>a.Id.StartsWith("auxiliary-merge:")||a.Id.StartsWith("entrance-merge:"));
    }
    [Theory][InlineData(50)][InlineData(300)]
    public void SoftEntranceActionIsTwoHundredMetresBeforeDeathButNeverBeforeItsStart(double length)
    {
        var(scene,route)=Scene(length);var cue=Assert.Single(Plan(scene,route).Actions,a=>a.ApproachInterval is not null);
        // Lane and road reference polylines differ by about 8cm at this synthetic convergence.
        double expected=Math.Max(cue.ApproachInterval!.StartMeters,cue.ApproachInterval.EndMeters-200);
        Assert.InRange(cue.DistanceMeters,expected,expected+.1);
    }
    [Fact]
    public void ProductionRuntimeConsumesTheSameSoftEntranceFactsWithoutLegacyCorridorOverrides()
    {
        var(scene,route)=Scene(50);var expected=Assert.Single(Plan(scene,route).Actions,a=>a.ApproachInterval is not null);
        var now=DateTimeOffset.UtcNow;
        var sdk=RouteSnapshot.Parse([new(route[0].StartNodeUid,1000,60),new(route[0].EndNodeUid,900,50),
            new(route[1].EndNodeUid,800,40),new(route[2].EndNodeUid,0,0)],now);
        var data=new GameDataSnapshot(new(10,0,-4,.75,15,1000,true,false,1,now),sdk,RouteAvailability.Available,"native",true)
            {NavigationRoute=NavigationRouteState.Empty.Accept(sdk)};
        var actual=new NavigationRuntime().Process(data,scene,now);
        Assert.Contains(actual.Upcoming,a=>a.Id==expected.Id&&a.Kind==expected.Kind);
        Assert.DoesNotContain(actual.Upcoming,a=>a.Id.StartsWith("auxiliary-merge:"));
    }
}
