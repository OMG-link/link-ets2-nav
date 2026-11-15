using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using TruckLib.Models.Ppd;

namespace LinkEts2Nav.UnitTests;

public sealed class PrefabJourneyReturnTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (TruckLib.ScsMap.Map Map,Road Road,Prefab Prefab,RouteSnapshot Route) Return(double sdkMeters)
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var prefab=new Prefab{Nodes=[road.ForwardNode]};map.MapItems[prefab.Uid]=prefab;road.ForwardNode.ForwardItem=prefab;
        var route=RouteSnapshot.Parse(new[]{new RouteNode(road.Node.Uid,(float)(sdkMeters+600),0),
            new(road.ForwardNode.Uid,(float)(sdkMeters+300),0),new(road.ForwardNode.Uid,300,0),new(road.Node.Uid,0,0)},Now);
        return(map,road,prefab,route);
    }

    [Theory]
    [InlineData(3)] [InlineData(19)]
    public void RecordedRoundaboutReturnKeepsNativeDistanceAndSelectsTheMatchingLane(double scale)
    {
        const double inner=146.54605102539062,outer=166.6240930557251;
        var s=Return(inner*scale);
        int calls=0;
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1.028,
            prefabLengths:(prefab,entry,exit)=>{calls++;Assert.Same(s.Prefab,prefab);Assert.Equal(entry,exit);return [outer,inner];})
            .Estimate(s.Route);
        Assert.True(result.Ready,result.Failure);Assert.Equal(1,calls);
        Assert.Equal(200+inner,result.WorldMeters!.Value,6);
        Assert.Equal((200+inner)/(80*.8/3.6),result.RealSeconds!.Value,6);
    }

    [Fact]
    public void RepeatedInterfaceCannotBeDroppedOrEstimatedAsZeroWithoutNativeReturnGeometry()
    {
        var s=Return(439.6382);
        Assert.Contains("缺少原生路线查询",new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category")).Estimate(s.Route).Failure);
        Assert.Contains("没有有效闭环",new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),
            prefabLengths:(_,_,_)=>[]).Estimate(s.Route).Failure);
    }

    [Fact]
    public void APositiveReturnWithOneNativeCandidateNeedsNoSdkScaleGuess()
    {
        var s=Return(439.6382);
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),2,
            prefabLengths:(_,_,_)=>[double.NaN,-1,146.54605102539062]).Estimate(s.Route);
        Assert.True(result.Ready,result.Failure);Assert.Equal(346.5460510253906,result.WorldMeters!.Value,6);
    }

    [Fact]
    public void InterfaceMappingHonorsPrefabOriginAndFindsARealConnectedReturn()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(50,0,0),default);
        var prefab=new Prefab{Nodes=[road.Node,road.ForwardNode]};
        typeof(Prefab).GetProperty(nameof(Prefab.Origin))!.SetValue(prefab,(ushort)1);
        var descriptor=new PrefabDescriptor{Nodes=[new ControlNode{InputLines=[],OutputLines=[]},
            new ControlNode{InputLines=[0],OutputLines=[1]}],NavCurves=[
            new NavCurve{StartPosition=new(0,0,0),EndPosition=new(0,0,10),Length=10,NextLines={1}},
            new NavCurve{StartPosition=new(0,0,10),EndPosition=new(0,0,0),Length=11}]};
        Assert.Equal(new[]{21d},PrefabPassageLengths.Read(prefab,descriptor,road.Node.Uid,road.Node.Uid));
        descriptor.NavCurves[0].NextLines.Clear();
        Assert.Empty(PrefabPassageLengths.Read(prefab,descriptor,road.Node.Uid,road.Node.Uid));
    }

    [Fact]
    public void SingleInterfaceNativePrefabCanHaveAValidReturn()
    {
        var s=Return(20);
        var descriptor=new PrefabDescriptor{Nodes=[new ControlNode{InputLines=[0],OutputLines=[1]}],NavCurves=[
            new NavCurve{StartPosition=Vector3.Zero,EndPosition=Vector3.UnitZ,Length=10,NextLines={1}},
            new NavCurve{StartPosition=Vector3.UnitZ,EndPosition=Vector3.Zero,Length=10}]};
        Assert.Equal(new[]{20d},PrefabPassageLengths.Read(s.Prefab,descriptor,s.Road.ForwardNode.Uid,s.Road.ForwardNode.Uid));
    }
}
