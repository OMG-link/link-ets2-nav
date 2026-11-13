using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class RouteJourneyEstimatorTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static RouteSnapshot Route(params ulong[] ids)=>RouteSnapshot.Parse(ids.Select((uid,i)=>new RouteNode(uid,(ids.Length-i-1)*10000,9999)),Now);
    private static SpeedLimitResult Eighty(Road road,bool forward)=>new(80,"test category");

    [Theory]
    [InlineData(false,750)]
    [InlineData(true,250)]
    public void OrdinaryRoadUsesHostLengthAndSubtractsTravelledArcInEitherDirection(bool reverse,double expected)
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(1000,0,0),default);
        var route=reverse?Route(road.ForwardNode.Uid,road.Node.Uid):Route(road.Node.Uid,road.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(map,Eighty).Estimate(route,new(250,0,0),reverse?-Vector3.UnitX:Vector3.UnitX);
        Assert.True(estimate.Ready,estimate.Failure);Assert.Equal(expected,estimate.WorldMeters!.Value,2);
        Assert.Equal(expected/(80*.8/3.6),estimate.RealSeconds!.Value,2);
    }

    private static (TruckLib.ScsMap.Map Map,Road Before,Road After,Prefab Prefab) SplitScene()
    {
        var map=new TruckLib.ScsMap.Map();var a=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var b=Road.Add(map,new(200,0,100),new(500,0,100),default);
        var prefab=new Prefab{Nodes=[a.ForwardNode,b.Node]};map.MapItems[prefab.Uid]=prefab;
        a.ForwardNode.ForwardItem=prefab;b.Node.BackwardItem=prefab;
        return(map,a,b,prefab);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.3)]
    public void CompletePrefabSpanCrossesInternalUnknownUidAndAlwaysUsesInterfaceChord(double factor)
    {
        var s=SplitScene();var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(s.Map,(r,_)=>new(r.Uid==s.Before.Uid?80:60,"category"),factor)
            .Estimate(route,new(50,0,0),Vector3.UnitX);
        double prefab=Vector3.Distance(s.Before.ForwardNode.Position,s.After.Node.Position)*factor;
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(50+prefab+s.After.Length,estimate.WorldMeters!.Value,2);
        Assert.Equal(50/(80*.8/3.6)+(prefab+s.After.Length)/(60*.8/3.6),estimate.RealSeconds!.Value,2);
    }

    [Fact]
    public void UnknownUidCannotCrossAnUnrelatedKnownRoadBoundary()
    {
        var s=SplitScene();var other=Road.Add(s.Map,new(150,0,0),new(150,0,50),default);
        var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,other.Node.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid);
        Assert.False(new RouteJourneyEstimator(s.Map,Eighty).Estimate(route,new(50,0,0),Vector3.UnitX).Ready);
    }

    [Fact]
    public void AlreadyPassedRoadsDoNotRequireSpeedsAndCurrentRoadTravelIsSubtracted()
    {
        var s=SplitScene();var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(s.Map,(r,_)=>r.Uid==s.Before.Uid?new(null,"past category unavailable"):new(60,"category"))
            .Estimate(route,new(350,0,100),Vector3.UnitX);
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(150,estimate.WorldMeters!.Value,2);
        Assert.Equal(150/(60*.8/3.6),estimate.RealSeconds!.Value,2);
    }

    [Fact]
    public void OriginOnPrefabUsesOnlyItsRemainingCorrectedChord()
    {
        var s=SplitScene();var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(s.Map,Eighty,1.2).Estimate(route,new(150,0,50),Vector3.Normalize(new(1,0,1)));
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(Vector3.Distance(s.Before.ForwardNode.Position,s.After.Node.Position)*.6+s.After.Length,estimate.WorldMeters!.Value,2);
    }

    [Fact]
    public void MissingConnectionIsNotInventedAsStraightLine()
    {
        var s=SplitScene();s.Before.ForwardNode.ForwardItem=null!;s.After.Node.BackwardItem=null!;
        var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,s.After.Node.Uid,s.After.ForwardNode.Uid);
        Assert.Contains("连接缺失",new RouteJourneyEstimator(s.Map,Eighty).Estimate(route,new(50,0,0),Vector3.UnitX).Failure);
    }

    [Fact]
    public void SharedRoadAndPrefabEndpointPairIsAmbiguousRatherThanArbitrarilyChoosingOne()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(500,0,0),default);
        var prefab=new Prefab{Nodes=[road.Node,road.ForwardNode]};map.MapItems[prefab.Uid]=prefab;
        road.Node.BackwardItem=prefab;road.ForwardNode.ForwardItem=prefab;
        Assert.Contains("归属不唯一",new RouteJourneyEstimator(map,Eighty)
            .Estimate(Route(road.Node.Uid,road.ForwardNode.Uid),new(250,0,0),Vector3.UnitX).Failure);
    }

    [Fact]
    public void MissingSpeedAndIncompleteSdkTerminalStayUnreadyWithoutUsingSdkTimeOrScale()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(500,0,0),default);
        var route=Route(road.Node.Uid,road.ForwardNode.Uid);var estimator=new RouteJourneyEstimator(map,Eighty);
        Assert.False(estimator.Estimate(route with{MayBeTruncated=true},Vector3.Zero,Vector3.UnitX).Ready);
        Assert.False(estimator.Estimate(route with{Nodes=[route.Nodes[0],route.Nodes[1] with{DistanceToEndMeters=100}]},Vector3.Zero,Vector3.UnitX).Ready);
        Assert.False(new RouteJourneyEstimator(map,(_,_)=>new(null,"unknown category")).Estimate(route,Vector3.Zero,Vector3.UnitX).Ready);
    }

    [Fact]
    public void CurvedOrdinaryRoadUsesLengthRatherThanItsEndpointChord()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(100,0,100),default);
        road.Node.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI);
        road.ForwardNode.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2);
        // The host supplies the stored arc length independently of the endpoint chord.
        road.Length=180;
        var estimate=new RouteJourneyEstimator(map,Eighty).Estimate(Route(road.Node.Uid,road.ForwardNode.Uid),road.Node.Position,Vector3.UnitZ);
        Assert.True(estimate.Ready,estimate.Failure);Assert.Equal(road.Length,estimate.WorldMeters!.Value,2);
        Assert.True(estimate.WorldMeters>Vector3.Distance(road.Node.Position,road.ForwardNode.Position));
    }
}
