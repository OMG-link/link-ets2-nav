using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class ComponentMergeIdentityTests
{
    [Fact]
    public void CoincidentMergesKeepTheSharedCurveActuallyVisitedByTheLane()
    {
        var origin=new Vector3(50,0,0);
        var geometry=NativeLaneFixture.WithInterfaces(42,[
            new(0,Vector3.Zero,origin,new(50,0,80),new(50,0,0),[2,3],[]),
            new(1,new(0,0,-4),origin,new(50,0,4),new(50,0,4),[2,3],[]),
            new(2,origin,new(100,0,30),new(50,0,0),new(50,0,120),[],[0,1]),
            new(3,origin,new(100,0,0),new(50,0,0),new(50,0,0),[],[0,1])]);
        DirectedRoad Edge(ulong entry,ulong exit,int first,int last)
        {
            var points=new List<Vector3>();var starts=new List<int>();
            foreach(int id in new[]{first,last}){starts.Add(points.Count);var c=geometry.Curves.Single(c=>c.Index==id);points.AddRange(Enumerable.Range(0,33).Select(i=>c.Position(i/32f)));}
            var path=new PrefabLanePath(42,entry,exit,0,0,0,0,1,1,[first,last],points,new LanePolyline(points).Length)
                {NativeGeometry=geometry,CurveStartPointIndices=starts};
            return new($"prefab:000000000000002A:{entry}:{exit}",entry,exit,points)
                {LanePaths=[path],JoinPriority=new(10,[],[],8,"known main")};
        }
        var selected=Edge(11,20,0,3);
        var merges=LaneContinuity.Analyze(geometry).Points.Where(p=>p.Kind==LaneContinuityPointKind.Merge).ToArray();
        Assert.Equal(2,merges.Length);Assert.All(merges,p=>Assert.Equal(origin,p.Origin));
        var graph=new NativeLaneGraph([Edge(10,20,1,3),selected,Edge(10,30,1,2),Edge(11,30,0,2)]);
        var index=ComponentContinuityIndex.Build(graph);
        var fact=index.ForEntry(selected.Id)!;Assert.Equal(ComponentContinuityKind.Hard,fact.Kind);
        var lane=Assert.Single(fact.Lanes);
        Assert.Equal(3,lane.TurnSharedCurveIndex);
        Assert.Equal("lane-merge:000000000000002A:3",fact.StructuralConstraintId);
        Assert.Equal(NavigationSide.None,lane.Turn!.Side);
        Assert.Contains("coincident topology origins",lane.Turn.Failure);
        var projection=ComponentManeuverProjection.Build([selected],0,[fact],[],selected.LanePaths[0]);
        Assert.Empty(projection.Actions);
        var issue=Assert.IsType<MapQueryFailure>(Assert.Single(projection.Issues));
        Assert.Equal(MapQueryFailureKind.Ambiguous,issue.Kind);
        Assert.Equal(lane.Turn.Failure,issue.Reason);
        Assert.Single(projection.Unresolved);
    }
}
