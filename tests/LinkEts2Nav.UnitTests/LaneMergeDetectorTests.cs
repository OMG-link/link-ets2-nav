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
            {CurveStartPointIndices=[0,(int)length],CurveBlinkers=[lane==mainLane?LaneBlinker.NoBlinker:left?LaneBlinker.Right:LaneBlinker.Left,LaneBlinker.NoBlinker]};
        var native=NativeGeometry([Lane(mainLane,1,main),Lane(endLane,2,ending)]);
        var m=Lane(mainLane,1,main) with{NativeGeometry=native};var e=Lane(endLane,2,ending) with{NativeGeometry=native};
        var edge=new DirectedRoad("prefab:000000000000002A:0:1",10,20,main){LanePaths=[m,e]};
        var roadPoints=Enumerable.Range(0,(int)upstream+1).Select(i=>Position(length+upstream-i,0)).ToArray();
        var road=new DirectedRoad("road",5,10,roadPoints);
        return(edge,[road,edge],new([new(edge.Id,e,0)],[],true,null));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSourceWindowIsLaneReductionAndFindsCorrectSide(bool left)
    {
        var scene=Scene(left:left);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan));
        Assert.Equal(LaneMergeKind.LaneReduction,observation.Kind);
        Assert.Null(observation.ParallelDistanceMeters);
        Assert.True(observation.Origin!.BackwardMeters>=500);Assert.Equal(!left,observation.MoveLeft);
        Assert.Equal(1,observation.RecommendedLaneCount);
        Assert.Equal(observation.DistanceMeters,observation.EndDistanceMeters);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConsecutiveRoadItemsDoNotTruncateSourceHistory(bool curved)
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
        var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],[..sections,scene.Edge],scene.Plan));
        Assert.Equal(LaneMergeKind.LaneReduction,observation.Kind);
        Assert.Null(observation.ParallelDistanceMeters);Assert.True(observation.Origin!.BackwardMeters>=500);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ShortSharedSuffixKeepsEndingIdentityAndUsesTheApproachStart(bool reverseOrder)
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
        var native=NativeGeometry([continuing,ending]);
        continuing=continuing with{NativeGeometry=native};ending=ending with{NativeGeometry=native};
        var edge=new DirectedRoad("prefab:000000000000002A:0:1",10,20,left)
            {LanePaths=reverseOrder?[ending,continuing]:[continuing,ending]};
        var plan=new LaneNavigationPlan([new(edge.Id,ending,0)],[],true,null);
        var result=Assert.Single(new LaneMergeDetector().Find([edge],[edge],plan));
        Assert.Equal(1,result.EndingEntryLane);
        Assert.Equal(0,result.MainEntryLane);
        // Identity is unchanged. The new wording samples the approach's first
        // curve4, rather than the last incoming curve6 immediately before merge.
        var tail=native.Curves.Single(c=>c.Index==3);
        Assert.Equal(LaneTurnKind.ForwardLeft,LaneContinuity.DescribeTurn(native.Curves.Single(c=>c.Index==4),tail.StartTangent,tail.Start,true));
        Assert.Equal(LaneTurnKind.ForwardRight,LaneContinuity.DescribeTurn(native.Curves.Single(c=>c.Index==6),tail.StartTangent,tail.Start,true));
        Assert.Equal(4,Assert.Single(Assert.Single(LaneContinuity.Analyze(native).Points).Branches,b=>b.CurveIndex==6).TurnSourceCurveIndex);
        Assert.True(result.MoveLeft);
        Assert.Equal(1,result.RecommendedLaneCount);
    }

    [Fact]
    public void UnresolvedNativeUpstreamCannotBorrowAnOlderContinuingLane()
    {
        var scene=Scene(upstream:80);var road=scene.Graph[0];float z=road.Points[0].Z;
        PrefabLanePath Lane(int lane)=>new(43,30,5,lane,lane,lane,lane,2,2,[lane],
            [new(lane*4.5f,0,z+600),new(lane*4.5f,0,z)],600);
        var left=Lane(0);var right=Lane(1);
        var side=new PrefabLanePath(43,40,5,0,1,0,1,1,2,[3,1],
            [new(100,0,z+50),new(4.5f,0,z)],120);
        DirectedRoad[] upstream=[new("prefab:000000000000002B:0:1",30,5,left.Points){LanePaths=[left,right]},
            new("prefab:000000000000002B:2:1",40,5,side.Points){LanePaths=[side]}];
        var result=Assert.Single(new LaneMergeDetector().Find([scene.Edge],[..upstream,..scene.Graph],scene.Plan));
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);
        Assert.Null(result.ParallelDistanceMeters);
        Assert.NotNull(result.Origin!.MapIssue);
    }

    [Fact]
    public void AFullFiveHundredMetreSourceWindowIsSameOriginDespiteShortParallelShape()
    {
        var scene=Scene(shortApproach:true);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan));
        Assert.Equal(LaneMergeKind.LaneReduction,observation.Kind);Assert.Null(observation.ParallelDistanceMeters);
        Assert.True(observation.Origin!.BackwardMeters>=500);
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
        var plan=new LaneNavigationPlan([new(sideEdge.Id,side,0)],[],true,null);
        var observation=Assert.Single(new LaneMergeDetector().Find([sideEdge],[scene.Graph[0],sideRoad,mainEdge,sideEdge],plan));
        Assert.Equal(LaneMergeKind.Ramp,observation.Kind);Assert.False(observation.MoveLeft);
    }
    [Fact]
    public void MissingUpstreamCoverageDoesNotTurnLongUnknownRoadIntoRamp()
    {
        var scene=Scene(upstream:80);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan));
        Assert.Equal(LaneMergeKind.Uncertain,observation.Kind);Assert.Null(observation.ParallelDistanceMeters);
    }
    [Fact]
    public void NativeTangentsSelectContinuingLaneOnCurvedRoad()
    {
        var scene=Scene(curved:true);var observation=Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan));
        // The curved fixture starts west of the shared curve heading; native
        // start-tangent direction now takes precedence over a lamp-based left cue.
        Assert.Equal(0,observation.MainEntryLane);Assert.False(observation.MoveLeft);
    }
    [Fact]
    public void SameOriginReductionAlsoWarnsTheContinuingLane()
    {
        var scene=Scene(shortApproach:true);var main=scene.Edge.LanePaths[0];
        var plan=new LaneNavigationPlan([new(scene.Edge.Id,main,0)],[],true,null);
        Assert.Equal(LaneMergeKind.LaneReduction,Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,plan)).Kind);
    }

    [Theory]
    [InlineData(2,false,0,false,false)][InlineData(2,false,1,false,false)][InlineData(2,false,2,true,false)][InlineData(2,false,3,true,false)]
    [InlineData(2,true,0,false,false)][InlineData(2,true,1,false,false)][InlineData(2,true,2,true,false)][InlineData(2,true,3,true,false)]
    [InlineData(3,false,0,false,false)][InlineData(3,false,1,true,false)][InlineData(3,false,2,true,false)][InlineData(3,false,3,true,false)]
    [InlineData(2,false,0,false,true)][InlineData(2,false,1,false,true)][InlineData(2,false,2,true,true)][InlineData(2,false,3,true,true)]
    public void ReductionOnlyWarnsTheNativeSourceApproachGroupNotOtherLanesAtTheSameInput(int sourceCount,bool left,int selectedLane,bool expected,bool reverseIds)
    {
        var shape=Scene(left:left,upstream:80);var inputRoad=shape.Graph[0];
        var continuing=shape.Edge.LanePaths[0] with{EntryLane=left?3:2,EntryOrder=left?3:2,EntryLaneCount=4,ExitLane=2,ExitOrder=2,ExitLaneCount=3};
        var ending=shape.Edge.LanePaths[1] with{EntryLane=left?2:3,EntryOrder=left?2:3,EntryLaneCount=4,ExitLane=2,ExitOrder=2,ExitLaneCount=3};
        PrefabLanePath Other(int lane)=>continuing with{EntryLane=lane,EntryOrder=lane,ExitLane=lane,ExitOrder=lane,CurveIndices=[20+lane]};
        var edge=shape.Edge with{LanePaths=[Other(0),Other(1),continuing,ending]};
        if(reverseIds)edge=edge with{LanePaths=[..edge.LanePaths.Select(p=>p with{EntryLane=3-p.EntryLane})]};
        var native=NativeGeometry(edge.LanePaths);edge=edge with{LanePaths=[..edge.LanePaths.Select(p=>p with{NativeGeometry=native})]};
        float sourceZ=inputRoad.Points[0].Z;
        PrefabLanePath Source(ulong entrance,int input,int output)=>new(43,entrance,5,input,output,input,output,entrance==100?sourceCount:4-sourceCount,4,[100+output],
            [new((output-2)*4.5f,0,sourceZ+20),new((output-2)*4.5f,0,sourceZ)],20);
        var sourceA=new DirectedRoad("prefab:43:0:1",100,5,[new(0,0,sourceZ+20),new(0,0,sourceZ)])
            {LanePaths=[..Enumerable.Range(0,sourceCount).Select(i=>Source(100,i,4-sourceCount+i))]};
        var sourceB=sourceA with{Id="prefab:43:2:1",StartNodeUid=200,LanePaths=[..Enumerable.Range(0,4-sourceCount).Select(i=>Source(200,i,i))]};
        var sourceNative=NativeGeometry([..sourceA.LanePaths,..sourceB.LanePaths]);
        sourceA=sourceA with{LanePaths=[..sourceA.LanePaths.Select(p=>p with{NativeGeometry=sourceNative})]};
        sourceB=sourceB with{LanePaths=[..sourceB.LanePaths.Select(p=>p with{NativeGeometry=sourceNative})]};
        var coveredA=new DirectedRoad("source-A-road",300,100,[new(0,0,sourceZ+670),new(0,0,sourceZ+20)]);
        var coveredB=coveredA with{Id="source-B-road",StartNodeUid=400,EndNodeUid=200};
        DirectedRoad[] graph=[coveredA,coveredB,sourceA,sourceB,inputRoad,edge];
        var selected=edge.LanePaths.Single(p=>p.EntryOrder==selectedLane);
        var observations=new LaneMergeDetector().Find([edge],graph,new([new(edge.Id,selected,0)],[],true,null));
        Assert.Equal(expected,observations.Count==1);
        if(expected)
        {
            var origin=Assert.Single(observations).Origin!;
            Assert.Equal(LaneMergeKind.LaneReduction,origin.Kind);Assert.Equal(100UL,origin.SourceEntrance);
            Assert.Equal(10UL,origin.ApplicableEntryNode);Assert.Equal(Enumerable.Range(4-sourceCount,sourceCount).Select(i=>reverseIds?3-i:i).Order(),origin.ApplicableEntryLanes);
        }
    }
    [Fact]
    public void ReductionIsAStaticRouteEventIndependentOfEarlierDetectorCalls()
    {
        var scene=Scene(shortApproach:true);var detector=new LaneMergeDetector();
        Assert.Single(detector.Find([scene.Edge],scene.Graph,scene.Plan));
        var main=new LaneNavigationPlan([new(scene.Edge.Id,scene.Edge.LanePaths[0],0)],[],true,null);
        Assert.Single(detector.Find([scene.Edge],scene.Graph,main));
        Assert.Single(detector.Find([scene.Edge],scene.Graph,main));
        Assert.Single(detector.Find([scene.Edge],scene.Graph,main));
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
        GameDataSnapshot Data(double x,double z,DateTimeOffset time)=>new(new(x,0,z,0,15,2100,true,false,1,time),route,RouteAvailability.Available,"s",true){NavigationRoute=NavigationRouteState.Empty.Accept(route)};
        int sounds=0;var spoken=new List<string>();var runtime=new NavigationRuntime(log:line=>{spoken.Add(line);if(line.StartsWith("\u5bfc\u822a\u6307\u4ee4\uff1a",StringComparison.Ordinal))sounds++;});
        var first=runtime.Process(Data(4.5,shortApproach?70:500,now),nearby,now);
        Assert.Contains(first.LaneMerges,m=>m.Kind==LaneMergeKind.LaneReduction);
        Assert.Equal(ManeuverKind.Destination,first.Next?.Kind);
        Assert.Contains(first.Upcoming,m=>m.AnnouncementMode==NavigationAnnouncementMode.DistancePreview);
        Assert.Contains(spoken,s=>s.Contains("车道合并，建议提前走左侧车道。"));
        runtime.Process(Data(1,10,now.AddSeconds(1)),nearby,now.AddSeconds(1));
        var after=runtime.Process(Data(0,-20,now.AddSeconds(2)),nearby,now.AddSeconds(2));
        Assert.DoesNotContain(after.LaneMerges,m=>m.PrefabUid==42);
        Assert.NotNull(after.LastSpoken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void NativeDistinctEntrancesClassifyTheActualSharedContractionWithoutGuessingAnApproach(int stretch)
    {
        var shape=Scene(shortApproach:true);
        Vector3 Scale(Vector3 p)=>p with{Z=p.Z*stretch};
        var map=new TruckLib.ScsMap.Map();
        var ending=shape.Edge.LanePaths[1] with{Points=shape.Edge.LanePaths[1].Points.Select(Scale).ToArray(),EntryLane=0,EntryOrder=0,EntryLaneCount=1};
        var main=shape.Edge.LanePaths[0] with{Points=shape.Edge.LanePaths[0].Points.Select(Scale).ToArray(),EntryLane=0,EntryOrder=0,EntryLaneCount=1};
        var sideRoad=Road.Add(map,ending.Points[0]+new Vector3(0,0,500),ending.Points[0],default);
        var mainRoad=Road.Add(map,main.Points[0]+new Vector3(0,0,500),main.Points[0],default);
        var outgoing=Road.Add(map,main.Points[^1],main.Points[^1]-new Vector3(0,0,2000),default);
        ending=ending with{EntryNodeUid=sideRoad.ForwardNode.Uid,ExitNodeUid=outgoing.Node.Uid};
        main=main with{EntryNodeUid=mainRoad.ForwardNode.Uid,ExitNodeUid=outgoing.Node.Uid};
        DirectedRoad Passage(string id, PrefabLanePath lane)
        {
            var curves = lane.NativeGeometry!.Curves;
            return shape.Edge with
            {
                Id = id, StartNodeUid = lane.EntryNodeUid, EndNodeUid = lane.ExitNodeUid,
                Points = lane.Points, LanePaths = [lane],
                NativeEntryTangent = Scale(curves.Single(c => c.Index == lane.CurveIndices[0]).StartTangent),
                NativeExitTangent = Scale(curves.Single(c => c.Index == lane.CurveIndices[^1]).EndTangent)
            };
        }
        var selected=Passage(shape.Edge.Id,ending);
        var continuing=Passage("prefab:000000000000002A:2:1",main);
        var now=DateTimeOffset.UtcNow;
        var route=RouteSnapshot.Parse([new(sideRoad.Node.Uid,3000,100),new(sideRoad.ForwardNode.Uid,2500,90),new(outgoing.Node.Uid,2000,80),new(outgoing.ForwardNode.Uid,0,0)],now);
        var data=new GameDataSnapshot(new(ending.Points[0].X,0,ending.Points[0].Z+100,0,15,3000,true,false,1,now),route,RouteAvailability.Available,"s",true){NavigationRoute=NavigationRouteState.Empty.Accept(route)};
        var diagnostic=new NavigationRuntime().Process(data,new(map,[],[],[]){PrefabPaths=[selected,continuing]},now);
        var merge=Assert.Single(diagnostic.LaneMerges);Assert.Equal(LaneMergeKind.Ramp,merge.Kind);
        Assert.DoesNotContain(diagnostic.Upcoming,a=>a.Kind is ManeuverKind.MergeLeft or ManeuverKind.MergeRight);
        Assert.DoesNotContain(diagnostic.Upcoming,a=>a.AnnouncementMode==NavigationAnnouncementMode.DistancePreview);
    }
    [Fact]
    public void RebuildingAStaticPlanKeepsTheCompleteMergeEvent()
    {
        var scene=Scene();Assert.Single(new LaneMergeDetector().Find([scene.Edge],scene.Graph,scene.Plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrContradictoryLampsDoNotOverrideNativeContinuity(bool contradictory)
    {
        var scene=Scene();
        var lanes=scene.Edge.LanePaths.Select(p=>p with
        {CurveBlinkers=[p.EntryLane==1&&contradictory?LaneBlinker.Right:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]}).ToArray();
        var edge=scene.Edge with{LanePaths=lanes};
        var plan=scene.Plan with{Sections=[new(edge.Id,lanes[1],0)]};
        var merge=Assert.Single(new LaneMergeDetector().Find([edge],[scene.Graph[0],edge],plan));
        Assert.Equal(LaneMergeKind.LaneReduction,merge.Kind);
        Assert.True(merge.MoveLeft);Assert.Equal(1,merge.EndingEntryLane);Assert.Equal(0,merge.MainEntryLane);
        Assert.Equal(1,merge.RecommendedLaneCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeftLaneRightBlinkerCannotOverrideNativeSurvival(bool reverseOrder)
    {
        // Lamp commands are deliberately contrary to this fixture's native
        // tangents. Latest local tools decide survival without consulting lamps.
        var scene=Scene();
        var left=scene.Edge.LanePaths[0] with{CurveBlinkers=[LaneBlinker.Right,LaneBlinker.NoBlinker]};
        var right=scene.Edge.LanePaths[1] with{CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        var edge=scene.Edge with{LanePaths=reverseOrder?[right,left]:[left,right]};
        var plan=new LaneNavigationPlan([new(edge.Id,left,0)],[],true,null);
        var merge=Assert.Single(new LaneMergeDetector().Find([edge],[scene.Graph[0],edge],plan));
        Assert.Equal(1,merge.EndingEntryLane);Assert.Equal(0,merge.MainEntryLane);
        Assert.True(merge.MoveLeft);Assert.Equal(1,merge.RecommendedLaneCount);
    }

    // Synthetic curve geometry for these polyline fixtures. Explicit segment
    // boundaries retain shared suffix identity; endpoint derivatives are fitted
    // from the adjacent distinct samples, rather than copied from blinkers.
    private static NativeLaneGeometry NativeGeometry(IReadOnlyList<PrefabLanePath> paths)
    {
        var curves=new Dictionary<int,NativeLaneCurve>();
        foreach(var path in paths)
        for(int i=0;i<path.CurveIndices.Count;i++)
        {
            int start=path.CurveStartPointIndices.Count==path.CurveIndices.Count?path.CurveStartPointIndices[i]:0;
            int end=i+1<path.CurveIndices.Count&&path.CurveStartPointIndices.Count==path.CurveIndices.Count
                ?path.CurveStartPointIndices[i+1]:path.Points.Count-1;
            var points=path.Points.Skip(start).Take(end-start+1).Distinct().ToArray();
            Assert.True(points.Length>=2,"Native test segments need two distinct control positions.");
            float length=(float)new LanePolyline(points).Length;
            Vector3 tangentStart=Vector3.Normalize(points[1]-points[0])*length;
            Vector3 tangentEnd=Vector3.Normalize(points[^1]-points[^2])*length;
            int index=path.CurveIndices[i];
            int[] previous=i>0?[path.CurveIndices[i-1]]:[];
            int[] next=i+1<path.CurveIndices.Count?[path.CurveIndices[i+1]]:[];
            if(curves.TryGetValue(index,out var old))
                curves[index]=old with{Previous=[..old.Previous.Concat(previous).Distinct()],Next=[..old.Next.Concat(next).Distinct()]};
            else curves[index]=new(index,points[0],points[^1],tangentStart,tangentEnd,next,previous);
        }
        return new(paths[0].PrefabUid,curves.Values.OrderBy(c=>c.Index).ToArray())
        {
            InputCurveIndices=paths.Select(p=>p.CurveIndices[0]).Distinct().ToArray(),
            OutputCurveIndices=paths.Select(p=>p.CurveIndices[^1]).Distinct().ToArray()
        };
    }
}
