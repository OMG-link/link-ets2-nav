using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class LaneMergeDetectorTests
{
    private static (DirectedRoad Edge,DirectedRoad[] Graph,LaneNavigationPlan Plan) Scene(bool shortApproach=false,bool left=false,double upstream=650,bool curved=false)
    {
        float sign=left?-1:1;double length=shortApproach?110:45;
        Vector3 Position(double d,double lateral)
        {
            if(!curved)return new((float)lateral,0,(float)d);
            const double radius=300;double angle=d/radius;
            return new((float)(radius*(1-Math.Cos(angle))+lateral*Math.Cos(angle)),0,(float)(radius*Math.Sin(angle)-lateral*Math.Sin(angle)));
        }
        var main=new List<Vector3>();var ending=new List<Vector3>();
        for(double d=length;d>=0;d-=1)
        {
            double t=Math.Clamp(d/30,0,1);double lateral=4.5*(3*t*t-2*t*t*t);
            if(shortApproach&&d>80)lateral+=(d-80)*.2;
            main.Add(Position(d,0));ending.Add(Position(d,sign*lateral));
        }
        for(int d=-1;d>=-10;d--){var common=Position(d,0);main.Add(common);ending.Add(common);}
        int mainLane=left?1:0;int endLane=left?0:1;
        PrefabLanePath Lane(int lane,int curve,List<Vector3> points)=>new(42,10,20,lane,0,lane,0,2,1,[curve,9],points,100);
        var m=Lane(mainLane,1,main);var e=Lane(endLane,2,ending);
        var edge=new DirectedRoad("prefab:000000000000002A:0:1",10,20,main){LanePaths=[m,e]};
        var roadPoints=Enumerable.Range(0,(int)upstream+1).Select(i=>Position(length+upstream-i,0)).ToArray();
        var road=new DirectedRoad("road",5,10,roadPoints);
        return(edge,[road,edge],new([new(edge.Id,e)],[],true,null));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongParallelApproachIsLaneReductionAndFindsCorrectSide(bool left)
    {
        var scene=Scene(left:left);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(LaneMergeKind.LaneReduction,observation.Kind);
        Assert.Equal(500,observation.ParallelDistanceMeters);Assert.Equal(!left,observation.MoveLeft);
        Assert.Equal(1,observation.RecommendedLaneCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConsecutiveRoadItemsDoNotTruncateParallelHistory(bool curved)
    {
        var scene=Scene(curved:curved);var source=scene.Graph[0];
        var sections=new List<DirectedRoad>();
        for(int start=0;start<source.Points.Count-1;start+=80)
        {
            int end=Math.Min(start+80,source.Points.Count-1);
            ulong from=start==0?source.StartNodeUid:(ulong)(1000+start);
            ulong to=end==source.Points.Count-1?source.EndNodeUid:(ulong)(1000+end);
            sections.Add(new("road-"+start,from,to,source.Points.Skip(start).Take(end-start+1).ToArray()));
        }
        var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],[..sections,scene.Edge],scene.Plan,0));
        Assert.Equal(LaneMergeKind.LaneReduction,observation.Kind);
        Assert.Equal(500,observation.ParallelDistanceMeters);
    }

    [Fact]
    public void ShortParallelApproachWithKnownDivergenceIsRamp()
    {
        var scene=Scene(shortApproach:true);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(LaneMergeKind.Ramp,observation.Kind);Assert.InRange(observation.ParallelDistanceMeters!.Value,70,110);
    }
    [Fact]
    public void MissingUpstreamCoverageDoesNotTurnLongUnknownRoadIntoRamp()
    {
        var scene=Scene(upstream:80);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(LaneMergeKind.Uncertain,observation.Kind);Assert.Null(observation.ParallelDistanceMeters);
    }
    [Fact]
    public void CurvedMainRoadStillWinsOverSidewaysConvergence()
    {
        var scene=Scene(curved:true);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(0,observation.MainEntryLane);Assert.True(observation.MoveLeft);
    }
    [Fact]
    public void RampDoesNotBlockDriverAlreadyOnContinuingLane()
    {
        var scene=Scene(shortApproach:true);var main=scene.Edge.LanePaths[0];
        var plan=new LaneNavigationPlan([new(scene.Edge.Id,main)],[],true,null);
        Assert.Empty(new LaneMergeDetector().Find([scene.Edge],scene.Graph,plan,0));
    }
    [Fact]
    public void RampApproachDoesNotDisappearWhenLaneSelectionTemporarilyChanges()
    {
        var scene=Scene(shortApproach:true);var detector=new LaneMergeDetector();
        Assert.Single(detector.Find([scene.Edge],scene.Graph,scene.Plan,0));
        var main=new LaneNavigationPlan([new(scene.Edge.Id,scene.Edge.LanePaths[0])],[],true,null);
        Assert.Single(detector.Find([scene.Edge],scene.Graph,main,10));
        Assert.Empty(detector.Find([scene.Edge],scene.Graph,main,1000));
        Assert.Empty(detector.Find([scene.Edge],scene.Graph,main,0));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RuntimeSchedulesMergeAndResumesRoadGuidanceAfterPassing(bool shortApproach)
    {
        var shape=Scene(shortApproach:shortApproach);var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,new(0,0,700),new(0,0,shape.Edge.Points[0].Z),default);
        var outgoing=Road.Add(map,new(0,0,-10),new(0,0,-2010),default);
        var edge=shape.Edge with {StartNodeUid=incoming.ForwardNode.Uid,EndNodeUid=outgoing.Node.Uid,
            LanePaths=shape.Edge.LanePaths.Select(p=>p with {EntryNodeUid=incoming.ForwardNode.Uid,ExitNodeUid=outgoing.Node.Uid}).ToArray()};
        var nearby=new NearbyMapSnapshot(map,[],[],[]){PrefabPaths=[edge]};
        var now=new DateTimeOffset(2026,11,9,0,0,0,TimeSpan.Zero);
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,2310,100),new(incoming.ForwardNode.Uid,2120,90),
            new(outgoing.Node.Uid,2000,80),new(outgoing.ForwardNode.Uid,0,0)],now);
        GameDataSnapshot Data(double x,double z,DateTimeOffset time)=>new(new(x,0,z,0,15,2100,true,false,1,time),route,RouteAvailability.Available,"s",true);
        int sounds=0;var runtime=new NavigationRuntime(fallback:()=>sounds++);
        var first=runtime.Process(Data(4.5,shortApproach?70:500,now),nearby,now);
        Assert.Contains(first.LaneMerges,m=>m.Kind==(shortApproach?LaneMergeKind.Ramp:LaneMergeKind.LaneReduction));
        Assert.Equal(shortApproach?NavigationAnnouncementMode.Silent:NavigationAnnouncementMode.NoticeAt500,first.Next!.AnnouncementMode);
        if(shortApproach)Assert.Null(first.LastSpoken);else Assert.Equal("请注意前方车道合并，建议走左侧车道。",first.LastSpoken);
        Assert.Equal(shortApproach?0:1,sounds);
        runtime.Process(Data(1,10,now.AddSeconds(1)),nearby,now.AddSeconds(1));
        Assert.Equal(shortApproach?0:1,sounds);
        var after=runtime.Process(Data(0,-20,now.AddSeconds(2)),nearby,now.AddSeconds(2));
        Assert.DoesNotContain(after.LaneMerges,m=>m.PrefabUid==42);
        Assert.NotNull(after.LastSpoken);Assert.Equal(shortApproach?1:2,sounds);
    }
    [Fact]
    public void PassingMergeRemovesItsAction()
    {
        var scene=Scene();Assert.Empty(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,1000));
    }
}
