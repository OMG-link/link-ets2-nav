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
    [InlineData(false)] [InlineData(true)]
    public void OverviewUsesEntireRoadAndDirectedCategoryWithoutVehiclePose(bool reverse)
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(1000,0,0),default);
        var route=reverse?Route(road.ForwardNode.Uid,road.Node.Uid):Route(road.Node.Uid,road.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(map,(_,forward)=>new(forward?80:60,"category")).Estimate(route);
        Assert.True(estimate.Ready,estimate.Failure);Assert.Equal(1000,estimate.WorldMeters!.Value,2);
        Assert.Equal(1000/((reverse?60:80)*.8/3.6),estimate.RealSeconds!.Value,2);
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
    [InlineData(1)] [InlineData(1.028)]
    public void CompletePrefabSpanCrossesInternalUnknownUidAndUsesCorrectedChord(double factor)
    {
        var s=SplitScene();var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(s.Map,(r,_)=>new(r.Uid==s.Before.Uid?80:60,"category"),factor).Estimate(route);
        double prefab=Vector3.Distance(s.Before.ForwardNode.Position,s.After.Node.Position)*factor;
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(s.Before.Length+prefab+s.After.Length,estimate.WorldMeters!.Value,2);
        Assert.Equal(s.Before.Length/(80*.8/3.6)+(prefab+s.After.Length)/(60*.8/3.6),estimate.RealSeconds!.Value,2);
    }

    [Fact]
    public void RouteStartingInsideAPrefabNeedsNoPositionProjection()
    {
        var s=SplitScene();
        var route=Route(s.Before.ForwardNode.Uid,999,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(s.Map,Eighty,1.028).Estimate(route);
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(Vector3.Distance(s.Before.ForwardNode.Position,s.After.Node.Position)*1.028+s.After.Length,estimate.WorldMeters!.Value,2);
    }

    [Fact]
    public void EveryVisitCountsEvenWhenTheRouteReusesTheSameRoad()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(500,0,0),default);
        var route=Route(road.Node.Uid,road.ForwardNode.Uid,road.Node.Uid,road.ForwardNode.Uid);
        var estimate=new RouteJourneyEstimator(map,Eighty).Estimate(route);
        Assert.True(estimate.Ready,estimate.Failure);
        Assert.Equal(1500,estimate.WorldMeters!.Value,2);
    }

    [Fact]
    public void SdkScaledDistancesAndTimesDoNotChangeWorldOverview()
    {
        var s=SplitScene();var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid);
        var estimator=new RouteJourneyEstimator(s.Map,Eighty,1.028);
        var scaled=route with{Nodes=route.Nodes.Select(n=>n with{DistanceToEndMeters=n.DistanceToEndMeters*19,TimeToEndSeconds=n.TimeToEndSeconds*60}).ToArray()};
        Assert.Equal(estimator.Estimate(route),estimator.Estimate(scaled));
    }

    [Fact]
    public void UnknownUidCannotCrossAnUnrelatedKnownRoadBoundary()
    {
        var s=SplitScene();var other=Road.Add(s.Map,new(150,0,0),new(150,0,50),default);
        var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,other.Node.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid);
        Assert.False(new RouteJourneyEstimator(s.Map,Eighty).Estimate(route).Ready);
    }

    [Fact]
    public void MissingConnectionIsNotInventedAsStraightLine()
    {
        var s=SplitScene();s.Before.ForwardNode.ForwardItem=null!;s.After.Node.BackwardItem=null!;
        var route=Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,999,s.After.Node.Uid,s.After.ForwardNode.Uid);
        Assert.Contains("连接缺失",new RouteJourneyEstimator(s.Map,Eighty).Estimate(route).Failure);
    }

    [Fact]
    public void SharedRoadAndPrefabEndpointPairIsAmbiguousRatherThanArbitrarilyChoosingOne()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(500,0,0),default);
        var prefab=new Prefab{Nodes=[road.Node,road.ForwardNode]};map.MapItems[prefab.Uid]=prefab;
        road.Node.BackwardItem=prefab;road.ForwardNode.ForwardItem=prefab;
        Assert.Contains("归属不唯一",new RouteJourneyEstimator(map,Eighty).Estimate(Route(road.Node.Uid,road.ForwardNode.Uid)).Failure);
    }

    [Fact]
    public void MissingSpeedAndIncompleteSdkTerminalStayUnreadyWithoutUsingSdkTimeOrScale()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(500,0,0),default);
        var route=Route(road.Node.Uid,road.ForwardNode.Uid);var estimator=new RouteJourneyEstimator(map,Eighty);
        Assert.False(estimator.Estimate(route with{MayBeTruncated=true}).Ready);
        Assert.False(estimator.Estimate(route with{Nodes=[route.Nodes[0],route.Nodes[1] with{DistanceToEndMeters=100}]}).Ready);
        Assert.False(new RouteJourneyEstimator(map,(_,_)=>new(null,"unknown category")).Estimate(route).Ready);
    }

    [Fact]
    public void CurvedOrdinaryRoadUsesStoredLengthRatherThanItsEndpointChord()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(100,0,100),default);
        road.Node.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI);
        road.ForwardNode.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2);
        road.Length=180;
        var estimate=new RouteJourneyEstimator(map,Eighty).Estimate(Route(road.Node.Uid,road.ForwardNode.Uid));
        Assert.True(estimate.Ready,estimate.Failure);Assert.Equal(180,estimate.WorldMeters!.Value,2);
    }
}
