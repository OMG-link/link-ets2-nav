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
        PrefabLanePath Lane(int lane,int curve,List<Vector3> points)=>new(42,10,20,lane,0,lane,0,2,1,[curve,9],points,100)
            {CurveBlinkers=[lane==mainLane?LaneBlinker.NoBlinker:left?LaneBlinker.Right:LaneBlinker.Left,LaneBlinker.NoBlinker]};
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
        Assert.True(observation.ParallelDistanceMeters>500);Assert.Equal(!left,observation.MoveLeft);
        Assert.Equal(1,observation.RecommendedLaneCount);
        Assert.Equal(observation.DistanceMeters,observation.EndDistanceMeters);
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
        Assert.True(observation.ParallelDistanceMeters>500);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShortSharedSuffixDoesNotReverseTheEndingLane(bool reverseOrder)
    {
        // Saved two-to-one road: left lane continues, right lane ends;
        // the common downstream curve is only about 1.5 metres long.
        Vector3[] left=[new(35.5160f,0.0000f,-11.8240f),
            new(35.3160f,0.0000f,-11.5080f),
            new(35.1170f,0.0000f,-11.1880f),
            new(34.9180f,0.0000f,-10.8700f),
            new(34.7200f,0.0000f,-10.5550f),
            new(34.7200f,0.0000f,-10.5550f),
            new(33.9900f,0.0000f,-9.3900f),
            new(33.2540f,0.0000f,-8.2270f),
            new(32.5230f,0.0000f,-7.0620f),
            new(31.8100f,0.0000f,-5.8870f),
            new(31.8100f,0.0000f,-5.8870f),
            new(30.2770f,0.0000f,-3.3320f),
            new(28.7460f,0.0000f,-0.7730f),
            new(27.2230f,0.0000f,1.7850f),
            new(25.6950f,0.0000f,4.3480f),
            new(24.1760f,0.0000f,6.9060f),
            new(22.6560f,0.0000f,9.4700f),
            new(21.1370f,0.0000f,12.0350f),
            new(19.6170f,0.0000f,14.5980f),
            new(18.0980f,0.0000f,17.1600f),
            new(16.5800f,0.0000f,19.7230f),
            new(15.0600f,0.0000f,22.2850f),
            new(13.5350f,0.0000f,24.8480f),
            new(12.0100f,0.0000f,27.4100f),
            new(10.4900f,0.0000f,29.9700f),
            new(8.9570f,0.0000f,32.5270f),
            new(7.4260f,0.0000f,35.0820f),
            new(5.8900f,0.0000f,37.6370f),
            new(4.3500f,0.0000f,40.1880f),
            new(2.8100f,0.0000f,42.7340f),
            new(1.2580f,0.0000f,45.2800f),
            new(-0.2970f,0.0000f,47.8200f),
            new(-1.8600f,0.0000f,50.3600f),
            new(-3.4260f,0.0000f,52.8950f),
            new(-5.0000f,0.0000f,55.4200f),
            new(-5.0000f,0.0000f,55.4200f),
            new(-5.2000f,0.0000f,55.7420f),
            new(-5.4000f,0.0000f,56.0600f),
            new(-5.5980f,0.0000f,56.3800f),
            new(-5.7970f,0.0000f,56.6950f)];
        Vector3[] right=[new(31.7000f,0.0000f,-14.2070f),
            new(31.5000f,0.0000f,-13.8900f),
            new(31.3000f,0.0000f,-13.5740f),
            new(31.1000f,0.0000f,-13.2540f),
            new(30.9020f,0.0000f,-12.9380f),
            new(30.9020f,0.0000f,-12.9380f),
            new(30.1700f,0.0000f,-11.7730f),
            new(29.4340f,0.0000f,-10.6100f),
            new(28.7030f,0.0000f,-9.4450f),
            new(27.9920f,0.0000f,-8.2700f),
            new(27.9920f,0.0000f,-8.2700f),
            new(26.4800f,0.0000f,-5.6900f),
            new(25.0100f,0.0000f,-3.0900f),
            new(23.5800f,0.0000f,-0.4700f),
            new(22.1760f,0.0000f,2.1700f),
            new(20.8050f,0.0000f,4.8300f),
            new(19.4530f,0.0000f,7.4960f),
            new(18.1250f,0.0000f,10.1760f),
            new(16.8120f,0.0000f,12.8630f),
            new(15.5100f,0.0000f,15.5620f),
            new(14.2230f,0.0000f,18.2600f),
            new(12.9380f,0.0000f,20.9700f),
            new(11.6560f,0.0000f,23.6700f),
            new(10.3700f,0.0000f,26.3800f),
            new(9.0800f,0.0000f,29.0800f),
            new(7.7770f,0.0000f,31.7730f),
            new(6.4600f,0.0000f,34.4600f),
            new(5.1300f,0.0000f,37.1370f),
            new(3.7770f,0.0000f,39.8050f),
            new(2.4000f,0.0000f,42.4530f),
            new(0.9920f,0.0000f,45.0900f),
            new(-0.4500f,0.0000f,47.7070f),
            new(-1.9260f,0.0000f,50.3000f),
            new(-3.4400f,0.0000f,52.8750f),
            new(-5.0000f,0.0000f,55.4200f),
            new(-5.0000f,0.0000f,55.4200f),
            new(-5.2000f,0.0000f,55.7420f),
            new(-5.4000f,0.0000f,56.0600f),
            new(-5.5980f,0.0000f,56.3800f),
            new(-5.7970f,0.0000f,56.6950f)];
        var continuing=new PrefabLanePath(42,10,20,0,0,0,0,2,1,[0,1,2,3],left,80)
            {CurveStartPointIndices=[0,5,10,35],CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        var ending=new PrefabLanePath(42,10,20,1,0,1,0,2,1,[4,5,6,3],right,80)
            {CurveStartPointIndices=[0,5,10,35],CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.Left,LaneBlinker.NoBlinker]};
        var edge=new DirectedRoad("prefab:000000000000002A:0:1",10,20,left)
            {LanePaths=reverseOrder?[ending,continuing]:[continuing,ending]};
        var plan=new LaneNavigationPlan([new(edge.Id,ending)],[],true,null);
        var result=Assert.Single(new LaneMergeDetector().Find([edge],[edge],plan,0));
        Assert.Equal(1,result.EndingEntryLane);
        Assert.Equal(0,result.MainEntryLane);
        Assert.True(result.MoveLeft);
        Assert.Equal(1,result.RecommendedLaneCount);
    }

    [Fact]
    public void SideRoadJoiningAnUpstreamExitLaneDoesNotTruncateTheMainCorridor()
    {
        var scene=Scene(upstream:80);var road=scene.Graph[0];float z=road.Points[0].Z;
        PrefabLanePath Lane(int lane)=>new(43,30,5,lane,lane,lane,lane,2,2,[lane],
            [new(lane*4.5f,0,z+600),new(lane*4.5f,0,z)],600);
        var left=Lane(0);var right=Lane(1);
        var side=new PrefabLanePath(43,40,5,0,1,0,1,1,2,[3,1],
            [new(100,0,z+50),new(4.5f,0,z)],120);
        DirectedRoad[] upstream=[new("prefab:000000000000002B:0:1",30,5,left.Points){LanePaths=[left,right]},
            new("prefab:000000000000002B:2:1",40,5,side.Points){LanePaths=[side]}];
        var result=Assert.Single(new LaneMergeDetector().Find([scene.Edge],[..upstream,..scene.Graph],scene.Plan,0));
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);
        Assert.True(result.ParallelDistanceMeters>500);
    }

    [Fact]
    public void ShortParallelGeometryWithoutNativeSeparateOriginDoesNotProveRamp()
    {
        var scene=Scene(shortApproach:true);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(LaneMergeKind.Uncertain,observation.Kind);Assert.InRange(observation.ParallelDistanceMeters!.Value,70,110);
    }
    [Fact]
    public void SharedSuffixAndDistinctNativeEntrancesProveShortRamp()
    {
        var scene=Scene(shortApproach:true);
        var main=scene.Edge.LanePaths[0] with{EntryLane=0,EntryOrder=0,EntryLaneCount=1};
        var side=scene.Edge.LanePaths[1] with{EntryNodeUid=99,EntryLane=0,EntryOrder=0,EntryLaneCount=1};
        var mainEdge=scene.Edge with{LanePaths=[main]};
        var sideEdge=scene.Edge with{Id="prefab:000000000000002A:2:1",StartNodeUid=99,LanePaths=[side]};
        var sideRoad=scene.Graph[0] with{Id="side-road",StartNodeUid=6,EndNodeUid=99,
            Points=scene.Graph[0].Points.Select(p=>p+new Vector3(side.Points[0].X,0,0)).ToArray()};
        var plan=new LaneNavigationPlan([new(sideEdge.Id,side)],[],true,null);
        var observation=Assert.Single(new LaneMergeDetector().Find([sideEdge],[scene.Graph[0],sideRoad,mainEdge,sideEdge],plan,0));
        Assert.Equal(LaneMergeKind.Ramp,observation.Kind);Assert.True(observation.MoveLeft);
    }
    [Fact]
    public void MissingUpstreamCoverageDoesNotTurnLongUnknownRoadIntoRamp()
    {
        var scene=Scene(upstream:80);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,0));
        Assert.Equal(LaneMergeKind.Uncertain,observation.Kind);Assert.Null(observation.ParallelDistanceMeters);
    }
    [Fact]
    public void NativeBlinkerStillSelectsContinuingLaneOnCurvedRoad()
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
        int sounds=0;var spoken=new List<string>();var runtime=new NavigationRuntime(log:spoken.Add,fallback:()=>sounds++);
        var first=runtime.Process(Data(4.5,shortApproach?70:500,now),nearby,now);
        Assert.Contains(first.LaneMerges,m=>m.Kind==(shortApproach?LaneMergeKind.Uncertain:LaneMergeKind.LaneReduction));
        Assert.Equal(shortApproach?NavigationAnnouncementMode.Silent:NavigationAnnouncementMode.DistancePreview,first.Next!.AnnouncementMode);
        if(shortApproach)Assert.Null(first.LastSpoken);
        else
        {
            Assert.Contains(spoken,s=>s.StartsWith("导航指令：")&&s.Contains("车道合并，建议提前走左侧车道。"));
            Assert.Equal(NavigationPrompts.Continue(),first.LastSpoken);
        }
        Assert.Equal(shortApproach?0:2,sounds);
        runtime.Process(Data(1,10,now.AddSeconds(1)),nearby,now.AddSeconds(1));
        Assert.Equal(shortApproach?0:2,sounds);
        var after=runtime.Process(Data(0,-20,now.AddSeconds(2)),nearby,now.AddSeconds(2));
        Assert.DoesNotContain(after.LaneMerges,m=>m.PrefabUid==42);
        Assert.NotNull(after.LastSpoken);Assert.Equal(shortApproach?1:2,sounds);
    }
    [Fact]
    public void PassingMergeRemovesItsAction()
    {
        var scene=Scene();Assert.Empty(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan,1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrContradictoryLampsDoNotGuessUsingCurvature(bool contradictory)
    {
        var scene=Scene();
        var lanes=scene.Edge.LanePaths.Select(p=>p with
        {CurveBlinkers=[p.EntryLane==1&&contradictory?LaneBlinker.Right:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]}).ToArray();
        var edge=scene.Edge with{LanePaths=lanes};
        var plan=scene.Plan with{Sections=[new(edge.Id,lanes[1])]};
        var merge=Assert.Single(new LaneMergeDetector().Find([edge],[scene.Graph[0],edge],plan,0));
        Assert.Equal(LaneMergeKind.Uncertain,merge.Kind);
        Assert.Null(merge.MoveLeft);Assert.Null(merge.EndingEntryLane);Assert.Null(merge.MainEntryLane);
        Assert.Equal(0,merge.RecommendedLaneCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeftLaneRightBlinkerOverridesTheOldCurvaturePreference(bool reverseOrder)
    {
        // Regression pattern observed in un_fork_4m-1m_0-2_0-3_join_0-2_60m:
        // the left competing incoming curve signals right; the right one does not.
        var scene=Scene();
        var left=scene.Edge.LanePaths[0] with{CurveBlinkers=[LaneBlinker.Right,LaneBlinker.NoBlinker]};
        var right=scene.Edge.LanePaths[1] with{CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        var edge=scene.Edge with{LanePaths=reverseOrder?[right,left]:[left,right]};
        var plan=new LaneNavigationPlan([new(edge.Id,left)],[],true,null);
        var merge=Assert.Single(new LaneMergeDetector().Find([edge],[scene.Graph[0],edge],plan,0));
        Assert.Equal(0,merge.EndingEntryLane);Assert.Equal(1,merge.MainEntryLane);
        Assert.False(merge.MoveLeft);Assert.Equal(1,merge.RecommendedLaneCount);
    }
}
