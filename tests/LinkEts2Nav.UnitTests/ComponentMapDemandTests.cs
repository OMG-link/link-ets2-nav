using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class ComponentMapDemandTests
{
    private static DirectedRoad[] Family(ulong prefab,ulong first,float x)
    {
        Vector3 start=new(x,0,0),side=new(x+10,0,0),join=new(x,0,30),end=new(x,0,50);
        var geometry=new NativeLaneGeometry(prefab,[new(0,start,join,join-start,join-start,[2],[]),
            new(1,side,join,join-side,join-side,[2],[]),new(2,join,end,end-join,end-join,[],[0,1])]);
        DirectedRoad Edge(int curve,ulong input)
        {
            Vector3[] points=[geometry.Curves[curve].Start,join,end];
            var lane=new PrefabLanePath(prefab,input,first+2,0,0,0,0,1,1,[curve,2],points,50)
                {NativeGeometry=geometry,CurveStartPointIndices=[0,1]};
            return new($"prefab:{prefab:X16}:{input}:{first+2}",input,first+2,points)
                {LanePaths=[lane],NativeEntryTangent=geometry.Curves[curve].StartTangent,NativeExitTangent=end-join};
        }
        return [Edge(0,first),Edge(1,first+1)];
    }
    [Fact]
    public void StaticPriorityAndComponentConstructionDoNotWaitForUnvisitedObjects()
    {
        var local=new MapObjectKey(MapObjectKind.Item,83);var remote=new MapObjectKey(MapObjectKind.Item,3020);
        var source=Family(42,10,0).Concat(Family(43,100,15000)).ToArray();
        var reported=new List<MapQueryIssue>();
        var paths=new RoadPathSource(new(),source,missingConnection:uid=>new MissingMapObjects(
            [uid<100?local:remote],"known missing owner"),reportMapIssue:reported.Add);
        var graph=paths.GetNavigationGraph();
        Assert.Empty(reported);Assert.Empty(paths.MapIssues);
        Assert.All(graph,e=>Assert.NotEmpty(e.JoinPriority!.MapIssues));
        var index=ComponentContinuityIndex.Build(new(graph));
        Assert.Contains(index.Issues.OfType<MissingMapObjects>(),i=>i.Keys.Contains(remote));
        Assert.Empty(reported); // Keeping static diagnostics is not a load request.
        var planner=new ComponentManeuverPlanner();planner.UseIndex(graph,index);
        planner.Build([graph.Single(e=>e.StartNodeUid==10)],graph);
        foreach(var issue in planner.Issues)paths.ReportMapIssue(issue);
        Assert.NotEmpty(reported);
        Assert.All(reported.OfType<MissingMapObjects>(),i=>Assert.Equal([local],i.Keys));
        Assert.DoesNotContain(reported.OfType<MissingMapObjects>(),i=>i.Keys.Contains(remote));
        Assert.Equal(Vector3.Zero,Assert.Single(reported).QueryAnchor);
    }
}
