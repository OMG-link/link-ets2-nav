using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

// Small native graphs isolate the production call contracts. They are not
// recordings of a player or evidence of a particular game's map defect.
public sealed class StaticLaneRouteTests
{
    private static DirectedRoad[] Join()
    {
        NativeLaneCurve C(int i,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(i,a,b,b-a,b-a,next,previous);
        var native=new NativeLaneGeometry(42,[C(0,Vector3.Zero,new(90,0,0),[2],[]),
            C(1,new(0,0,30),new(90,0,0),[2],[]),C(2,new(90,0,0),new(100,0,0),[],[0,1])])
            {InputCurveIndices=[0,1],OutputCurveIndices=[2]};
        DirectedRoad Edge(int curve,ulong start)
        {
            Vector3[] points=[native.Curves[curve].Start,new(90,0,0),new(100,0,0)];
            var lane=new PrefabLanePath(42,start,30,0,0,0,0,1,1,[curve,2],points,new LanePolyline(points).Length)
                {NativeGeometry=native,CurveStartPointIndices=[0,1]};
            return new($"prefab:2A:{start}:30",start,30,points)
                {LanePaths=[lane],JoinPriority=new(10,[],[],8,"prepared main")};
        }
        return [Edge(0,10),Edge(1,20)];
    }

    [Fact]
    public void CompleteLanePlanKeepsTheOriginOfAnActionThatExtendsDownstream()
    {
        var group=Join();var road=new DirectedRoad("road",30,40,[new(100,0,0),new(200,0,0)]);
        var lane=new PrefabLanePath(77,40,50,0,0,0,0,1,1,[0],[new(200,0,0),new(300,0,0)],100);
        var tail=new DirectedRoad("prefab:4D:0:1",40,50,lane.Points){LanePaths=[lane]};
        DirectedRoad[] route=[group[1],road,tail];
        var selected=new LaneRoutePlanner().Build(route);
        Assert.True(selected.Complete);
        Assert.Equal(new[]{0,2},selected.Sections.Select(s=>s.SourceVisitIndex));
        var projected=new ComponentManeuverPlanner().Build(route,[..group,road,tail],selected);
        Assert.NotEmpty(projected[0]);
    }

    [Fact]
    public void AMarkedRingLeavesEarlierRawVisitSelectionsIntact()
    {
        var group=Join();var road=new DirectedRoad("road",30,40,[new(100,0,0),new(200,0,0)]);
        var lane=new PrefabLanePath(77,40,50,0,0,0,0,1,1,[0],[new(200,0,0),new(300,0,0)],100);
        var ringEdge=new DirectedRoad("prefab:4D:0:1",40,50,lane.Points){LanePaths=[lane]};
        DirectedRoad[] route=[group[1],road,ringEdge];
        var selected=new LaneRoutePlanner().Build(route);
        var range=new RoundaboutRouteRange("ring",2,3,RouteGeometry.Length(group[1])+100,RouteGeometry.Length(group[1])+200,[]);
        var projected=new ComponentManeuverPlanner().Build(route,[..group,road,ringEdge],selected,
            excludeVisit:range.ContainsVisit);
        Assert.NotEmpty(projected[0]);Assert.False(projected.ContainsKey(2));
        Assert.Same(ringEdge,route[2]);
    }

    [Fact]
    public void ActualPositionAndLaneOnlyChangeProgressNotTheLanePlanOrActions()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,Vector3.Zero,new(0,0,700),default);
        var right=Road.Add(map,new(-30,0,730),new(-600,0,730),default);
        var straight=Road.Add(map,new(0,0,760),new(0,0,1300),default);
        Vector3[] points=[new(0,0,700),new(0,0,710),new(-10,0,725),new(-30,0,730)];
        var leftLane=new PrefabLanePath(0xABC,incoming.ForwardNode.Uid,right.Node.Uid,0,0,0,0,2,1,[0],points,50);
        var rightLane=leftLane with{EntryLane=1,EntryOrder=1,CurveIndices=[1],LengthMeters=80,
            Points=[new(-3.5f,0,700),new(-3.5f,0,710),new(-10,0,725),new(-30,0,730)]};
        var turn=new DirectedRoad("prefab:ABC:0:1",incoming.ForwardNode.Uid,right.Node.Uid,points)
        {LanePaths=[leftLane,rightLane],Junction=new(2,2,1),NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=-Vector3.UnitX};
        var through=new DirectedRoad("prefab:ABC:0:2",incoming.ForwardNode.Uid,straight.Node.Uid,[new(0,0,700),new(0,0,760)])
        {Junction=turn.Junction,NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ};
        var nearby=new NearbyMapSnapshot(map,[],[],[]){PrefabPaths=[turn,through]};
        var sdk=RouteSnapshot.Parse([new(incoming.Node.Uid,1300,60),new(incoming.ForwardNode.Uid,600,30),
            new(right.Node.Uid,570,28),new(right.ForwardNode.Uid,0,0)],now);
        GameDataSnapshot Data(double x,double z)=>new(new(x,0,z,.5,20,1000,true,false,1,now),sdk,RouteAvailability.Available,"same-session",true){NavigationRoute=NavigationRouteState.Empty.Accept(sdk)};
        var runtime=new NavigationRuntime();
        var first=runtime.Process(Data(0,100),nearby,now);int builds=runtime.StructureBuildCount;
        var second=runtime.Process(Data(-3.5,300),nearby,now);
        Assert.NotNull(first.LaneRoute);Assert.Same(first.LaneRoute,second.LaneRoute);
        Assert.Equal(0,Assert.Single(first.LaneRoute.Sections).Path.EntryOrder);
        Assert.Equal(builds,runtime.StructureBuildCount);
        Assert.Equal(first.Next!.Id,second.Next!.Id);
        Assert.Equal(first.Next.Kind,second.Next.Kind);
        Assert.True(second.Next.DistanceMeters<first.Next.DistanceMeters);
        Assert.Equal(first.Next.DistanceMeters+100,second.Next.DistanceMeters+300,3);
    }

    [Fact]
    public void KnownSdkComponentsArePlannedBeforePlayerMatchingAndRetainedAcrossProgress()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var first=Road.Add(map,Vector3.Zero,new(0,0,1000),default);
        var second=Road.Add(map,new(1000,0,0),new(1000,0,1000),default);
        var sdk=RouteSnapshot.Parse([new(first.Node.Uid,3000,90),new(first.ForwardNode.Uid,2000,60),
            new(second.Node.Uid,1000,30),new(second.ForwardNode.Uid,0,0)],now);
        var nearby=new NearbyMapSnapshot(map,[],[],[])
            {MissingConnection=_=>new MissingMapObjects([new(MapObjectKind.Item,999)],"fixture known SDK gap")};
        GameDataSnapshot Data(double x)=>new(new(x,0,100,.5,20,1000,true,false,1,now),sdk,RouteAvailability.Available,"same-session",true){NavigationRoute=NavigationRouteState.Empty.Accept(sdk)};
        var runtime=new NavigationRuntime();
        runtime.Process(Data(0),nearby,now);
        Assert.Equal(2,runtime.StructureBuildCount);
        runtime.Process(Data(1000),nearby,now);
        Assert.Equal(2,runtime.StructureBuildCount);
    }
}
