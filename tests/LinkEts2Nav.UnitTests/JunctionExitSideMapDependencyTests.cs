using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class JunctionExitSideMapDependencyTests
{
    private static DirectedRoad Passage(ulong from,ulong to,int[] chain,NativeLaneGeometry geometry,
        RoadJoinPriority? join=null,RoadJoinPriority? split=null,int entryOrder=0)
    {
        var points=new List<Vector3>();var starts=new List<int>();
        foreach(int id in chain)
        {
            starts.Add(points.Count);var curve=geometry.Curves.Single(c=>c.Index==id);
            points.AddRange(Enumerable.Range(0,17).Select(i=>curve.Position(i/16f)));
        }
        var path=new PrefabLanePath(42,from,to,entryOrder,0,entryOrder,0,2,1,chain,points,new LanePolyline(points).Length)
            {NativeGeometry=geometry,CurveStartPointIndices=starts};
        return new($"prefab:000000000000002A:{from}:{to}",from,to,points)
            {LanePaths=[path],JoinPriority=join,SplitPriority=split};
    }
    private static NativeLaneCurve Curve(int id,Vector3 start,Vector3 end,int[] next,int[] previous)
        =>new(id,start,end,end-start,end-start,next,previous);

    [Fact]
    public void ReverseWalkMissingUpstreamRemainsTypedThroughTheProductionProjection()
    {
        var geometry=NativeLaneFixture.WithInterfaces(42,
            [Curve(0,Vector3.Zero,new(100,0,0),[],[]),Curve(1,new(0,0,4),new(100,0,4),[],[])]);
        var priority=new RoadJoinPriority(20,[],[],8,"known main outlet");
        var main=Passage(10,20,[0],geometry,split:priority);
        var side=Passage(10,30,[1],geometry,split:priority,entryOrder:1);
        var missing=new MissingMapSectors([new(3,4)],"upstream window seam");
        var index=ComponentContinuityIndex.Build(new([main,side]),_=>missing);
        Assert.Equal(ComponentContinuityKind.Main,index.ForExit(main.Id)!.Kind);
        Assert.Equal(ComponentContinuityKind.Unknown,index.ForExit(side.Id)!.Kind);
        Assert.IsType<MissingMapSectors>(index.ForExit(side.Id)!.Issue);
        var result=ComponentManeuverProjection.Build([main],0,[],index.Exits,main.LanePaths[0]);
        Assert.Equal(ManeuverKind.Straight,Assert.Single(result.Actions).Kind);
        Assert.Equal(NavigationSide.None,result.Actions[0].StraightExitSides);
        Assert.IsType<MissingMapSectors>(Assert.Single(result.Issues));
        Assert.Equal(new NavigationUnresolvedSpan(0,RouteGeometry.Length(main)),Assert.Single(result.Unresolved));
        var planner=new ComponentManeuverPlanner();DirectedRoad[] graph=[main,side];
        planner.UseIndex(graph,index);
        Assert.Equal(ManeuverKind.Straight,Assert.Single(planner.Build([main],graph)[0]).Kind);
        Assert.IsType<MissingMapSectors>(Assert.Single(planner.Issues));
        Assert.Equal(result.Unresolved,planner.Unresolved);
    }

    [Fact]
    public void ReversePriorityMissingDoesNotInvalidateTheKnownEntrancePassage()
    {
        var geometry=NativeLaneFixture.WithInterfaces(42,
            [Curve(0,Vector3.Zero,new(40,0,0),[2],[]),Curve(1,new(0,0,15),new(40,0,0),[2],[]),
             Curve(2,new(40,0,0),new(60,0,0),[3,4],[0,1]),
             Curve(3,new(60,0,0),new(100,0,0),[],[2]),Curve(4,new(60,0,0),new(100,0,25),[],[2])]);
        var missing=new MissingMapSectors([new(3,4)],"outlet scoring window seam");
        var join=new RoadJoinPriority(10,[],[],8,"known main entrance");
        var split=new RoadJoinPriority(null,[],[],null,"outlet dependencies missing"){MapIssues=[missing]};
        DirectedRoad[] graph=[Passage(10,20,[0,2,3],geometry,join,split),Passage(10,30,[0,2,4],geometry,join,split),
            Passage(11,20,[1,2,3],geometry,join,split),Passage(11,30,[1,2,4],geometry,join,split)];
        var index=ComponentContinuityIndex.Build(new(graph));var selected=graph[0];
        var entry=index.ForEntry(selected.Id)!;
        Assert.Equal(ComponentContinuityKind.Main,entry.Kind);Assert.Equal(2,entry.LegalExitCount);
        Assert.All(index.Entries.Where(f=>f.LegalExitCount>=2),f=>Assert.NotNull(index.ForExit(f.EdgeId)));
        Assert.Equal(ComponentContinuityKind.Unknown,index.ForExit(selected.Id)!.Kind);
        Assert.IsType<MissingMapSectors>(index.ForExit(selected.Id)!.Issue);
        var result=ComponentManeuverProjection.Build([selected],0,[entry],index.Exits,selected.LanePaths[0]);
        Assert.Equal(ManeuverKind.Straight,Assert.Single(result.Actions).Kind);
        Assert.NotEmpty(result.Issues);Assert.All(result.Issues,i=>Assert.IsType<MissingMapSectors>(i));
        Assert.NotEmpty(result.Unresolved);
    }

    [Fact]
    public void SingleOutletEntranceDoesNotQueryTheIntentionallyOmittedReverseGroup()
    {
        var geometry=NativeLaneFixture.WithInterfaces(42,
            [Curve(0,Vector3.Zero,new(90,0,0),[2],[]),Curve(1,new(0,0,30),new(90,0,0),[2],[]),
             Curve(2,new(90,0,0),new(100,0,0),[],[0,1])]);
        var join=new RoadJoinPriority(10,[],[],8,"known main entrance");
        var main=Passage(10,20,[0,2],geometry,join);var side=Passage(11,20,[1,2],geometry,join);
        var index=ComponentContinuityIndex.Build(new([main,side]));var entry=index.ForEntry(main.Id)!;
        Assert.Equal(ComponentContinuityKind.Main,entry.Kind);Assert.Equal(1,entry.LegalExitCount);
        Assert.Null(index.ForExit(main.Id));Assert.Empty(index.Exits);
        var result=ComponentManeuverProjection.Build([main],0,[entry],index.Exits,main.LanePaths[0]);
        Assert.Empty(result.Actions);Assert.Empty(result.Issues);Assert.Empty(result.Unresolved);
    }

}
