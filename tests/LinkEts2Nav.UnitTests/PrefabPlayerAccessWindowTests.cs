using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class PrefabPlayerAccessWindowTests
{
    private static PrefabPermissionSource Straight(ulong uid,float x,ulong entry,ulong exit,bool reversedInputSlots=false,int origin=0)
    {
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new ControlNode{Position=Vector3.Zero,InputLines=reversedInputSlots?[1,0]:[0,1],OutputLines=[]},
                new ControlNode{Position=new(10,0,0),InputLines=[],OutputLines=[0,1]}],
            NavCurves=Enumerable.Range(0,2).Select(i=>new NavCurve{Length=10,StartPosition=new(0,0,i*4-2),EndPosition=new(10,0,i*4-2),
                StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2),EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2)}).ToList()
        };
        INode[] nodes=[new Node{Uid=entry,Position=new(x,0,0)},new Node{Uid=exit,Position=new(x+10,0,0)}];
        var prefab=new Prefab{Uid=uid,Nodes=nodes.ToList()};
        if(origin>0)prefab.ChangeOrigin((ushort)origin);
        return new(prefab,descriptor);
    }
    private static Road RoadAt(ulong node,float x)
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(x-10,0,0),new(x,0,0),default);
        road.ForwardNode.Uid=node;return road;
    }
    private static IReadOnlyDictionary<ulong,PrefabPlayerProjection> Build(PrefabPermissionSource[] sources,Road road,
        bool?[]? permissions,Action<string>? diagnostic=null)
        =>PrefabPlayerAccessWindow.Build(sources,(_,_)=>permissions,(_,_)=>null,_=>null,
            uid=>uid==road.ForwardNode.Uid?[road]:[],diagnostic);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BindsByDriverRelativeNativeEndpointOrderAndPreservesRawSlots(int origin)
    {
        var source=Straight(1,0,10,20,reversedInputSlots:true,origin:origin);
        var result=Build([source],RoadAt(10,0),[false,null])[1];
        Assert.Equal(1,result.ExcludedCurves);
        Assert.Equal(0,result.Descriptor.NavCurves[0].Length);Assert.Equal(10,result.Descriptor.NavCurves[1].Length);
        Assert.Equal(new[]{1,-1},result.Descriptor.Nodes[0].InputLines);
        Assert.Equal(new[]{-1,1},result.Descriptor.Nodes[1].OutputLines);
        Assert.Equal(new[]{1,0},source.Descriptor.Nodes[0].InputLines);
        Assert.Equal(10,source.Descriptor.NavCurves[0].Length);
    }
    [Fact]
    public void AProvenRailLanePropagatesAcrossNativePrefabAttachments()
    {
        var first=Straight(1,0,10,20);var second=Straight(2,10,20,30);var third=Straight(3,20,30,40);
        var result=Build([first,second,third],RoadAt(10,0),[false,null]);
        Assert.All(result.Values,p=>Assert.Equal(1,p.ExcludedCurves));
        Assert.All(result.Values,p=>Assert.Equal(10,p.Descriptor.NavCurves[1].Length));
    }
    [Fact]
    public void UnknownAndCarReachablePathsAreNotRemoved()
    {
        var first=Straight(1,0,10,20);var second=Straight(2,10,20,30);
        var unknown=Build([first,second],RoadAt(10,0),[null,null]);
        Assert.All(unknown.Values,p=>Assert.Equal(0,p.ExcludedCurves));
        first.Descriptor.NavCurves[0].NextLines.Add(1);
        var shared=Build([first,second],RoadAt(10,0),[false,null]);
        Assert.Equal(10,shared[1].Descriptor.NavCurves[1].Length);
        Assert.Equal(10,shared[2].Descriptor.NavCurves[1].Length);
    }
    [Fact]
    public void MismatchedLaneCountKeepsUnknownLanesAndReportsTheMissingBinding()
    {
        var source=Straight(1,0,10,20);var messages=new List<string>();
        var result=Build([source],RoadAt(10,0),[false,null,null],messages.Add)[1];
        Assert.Same(source.Descriptor,result.Descriptor);
        Assert.Contains(messages,m=>m.Contains("方向车道数或横向顺序不唯一",StringComparison.Ordinal));
    }
    [Fact]
    public void CoincidentEndpointsDoNotInventAnOrderForForbiddenLane()
    {
        var source=Straight(1,0,10,20);source.Descriptor.NavCurves[1].StartPosition=source.Descriptor.NavCurves[0].StartPosition;
        Assert.Equal(0,Build([source],RoadAt(10,0),[false,null])[1].ExcludedCurves);
    }
    [Fact]
    public void AWindowBoundaryWithoutEvidenceDoesNotCloseADisconnectedCycle()
    {
        var source=Straight(1,0,10,20);source.Descriptor.Nodes=[];source.Prefab.Nodes=[];
        source.Descriptor.NavCurves[0].NextLines.Add(1);source.Descriptor.NavCurves[1].NextLines.Add(0);
        Assert.Same(source.Descriptor,Build([source],RoadAt(40,0),[false,null])[1].Descriptor);
    }
    [Fact]
    public void AnExplicitRuleBlocksItsCurveEvenWithoutRoadBinding()
    {
        var source=Straight(1,0,10,20);source.Descriptor.NavCurves[1].TrafficRule="private";
        var result=PrefabPlayerAccessWindow.Build([source],(_,_)=>null,(_,_)=>null,r=>r=="private"?false:null,token:TestContext.Current.CancellationToken)[1];
        Assert.Equal(10,result.Descriptor.NavCurves[0].Length);Assert.Equal(0,result.Descriptor.NavCurves[1].Length);
    }
}
