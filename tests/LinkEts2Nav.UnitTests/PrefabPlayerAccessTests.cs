using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class PrefabPlayerAccessTests
{
    private static (Prefab Prefab,PrefabDescriptor Descriptor) Fork()
    {
        var d=new PrefabDescriptor
        {
            Nodes=[new ControlNode{Position=Vector3.Zero,InputLines=[0,1,2],OutputLines=[]},
                new ControlNode{Position=new(10,0,-4),InputLines=[],OutputLines=[0]},
                new ControlNode{Position=new(10,0,2),InputLines=[],OutputLines=[1,2]}],
            NavCurves=Enumerable.Range(0,3).Select(i=>new NavCurve{StartPosition=new(0,0,i*4-4),EndPosition=new(10,0,i*4-4),
                Length=10,StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2),
                EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2)}).ToList()
        };
        return(new Prefab{Uid=1,Nodes=d.Nodes.Select((n,i)=>(INode)new Node{Uid=(ulong)(10+i*10),Position=n.Position}).ToList()},d);
    }
    [Fact]
    public void DedicatedTramOutletIsRemovedBeforeForkAndLaneClassification()
    {
        var (prefab,native)=Fork();
        var original=PrefabPathGeometry.Build(prefab,native);
        var originalFork=original.First().ForkInfo!;
        Assert.Equal(3,originalFork.EntryLaneCount);Assert.Equal(2,originalFork.Branches.Count);
        var projection=PrefabPlayerAccess.Build(native,[null,null,null],[null,false,null]);
        var edges=PrefabPathGeometry.Build(prefab,projection.Descriptor);
        var edge=Assert.Single(edges);
        Assert.Equal(30UL,edge.EndNodeUid);
        Assert.Null(edge.ForkInfo); // one legal outlet is no longer a fork
        Assert.All(edge.LanePaths,p=>Assert.Equal(2,p.EntryLaneCount));
        Assert.Equal(new[]{1,2},edge.LanePaths.Select(p=>p.EntryLane).Order());
        Assert.Equal(new[]{0,1},edge.LanePaths.Select(p=>p.EntryOrder).Order());
        Assert.DoesNotContain(PrefabLaneTopology.Build(prefab,projection.Descriptor)!,p=>p.ExitNode==20);
        Assert.Equal(new[]{0,1,2},native.Nodes[0].InputLines);
        Assert.Equal(10,native.NavCurves[0].Length);
    }
    [Fact]
    public void UnknownOrSharedTramPermissionRetainsTheNativeOutlet()
    {
        var (_,native)=Fork();
        Assert.Same(native,PrefabPlayerAccess.Build(native,[null,null,null],[null,null,null]).Descriptor);
        Assert.Same(native,PrefabPlayerAccess.Build(native,[null,null,null],[null,true,null]).Descriptor);
    }
    [Fact]
    public void SharedCurvesReachedFromCarEntranceAndCarExitSurviveBlockedRailEntrance()
    {
        var d=new PrefabDescriptor
        {
            Nodes=[new ControlNode{InputLines=[0],OutputLines=[]},new ControlNode{InputLines=[1],OutputLines=[]},
                new ControlNode{InputLines=[],OutputLines=[2]}],
            NavCurves=[new NavCurve{Length=10,NextLines={2}},new NavCurve{Length=10,NextLines={2}},new NavCurve{Length=10}]
        };
        var projection=PrefabPlayerAccess.Build(d,[false,null,null],[null,null,null]);
        Assert.Equal(new[]{false,true,true},projection.AllowedCurves);
        Assert.Equal(new[]{2},projection.Descriptor.NavCurves[1].NextLines);
    }
    [Fact]
    public void AnExplicitInternalBanBreaksAPathEvenWhenItsPortsAreOpen()
    {
        var (_,d)=Fork();d.NavCurves[1].TrafficRule="private";
        var projection=PrefabPlayerAccess.Build(d,[null,null,null],[null,null,null],rule=>rule=="private"?false:null);
        Assert.Equal(new[]{true,false,true},projection.AllowedCurves);
    }
    [Fact]
    public void APathEndingOnlyAtAnInternalBanDoesNotLeaveAGhostEntranceLane()
    {
        var d=new PrefabDescriptor
        {
            Nodes=[new ControlNode{InputLines=[0,2],OutputLines=[]},new ControlNode{InputLines=[],OutputLines=[1,2]}],
            NavCurves=[new NavCurve{Length=10,NextLines={1}},new NavCurve{Length=10,TrafficRule="private"},new NavCurve{Length=10}]
        };
        var projection=PrefabPlayerAccess.Build(d,[null,null],[null,null],rule=>rule=="private"?false:null);
        Assert.Equal(new[]{-1,2},projection.Descriptor.Nodes[0].InputLines);
        Assert.True(projection.AllowedCurves![2]);
    }
    [Fact]
    public void AnUnknownDisconnectedCycleIsNotMistakenForForbiddenTraffic()
    {
        var d=new PrefabDescriptor{Nodes=[],NavCurves=[new NavCurve{Length=10,NextLines={1}},new NavCurve{Length=10,NextLines={0}}]};
        Assert.Same(d,PrefabPlayerAccess.Build(d,[],[]).Descriptor);
    }
    [Fact]
    public void RoadDirectionPermissionDoesNotCloseTheOtherDirection()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(100,0,0),default);
        var source=new RoadPathSource(map,playerAccess:(r,forward)=>forward?false:null);
        var edge=Assert.Single(source.GetNavigationGraph());
        Assert.Equal(road.ForwardNode.Uid,edge.StartNodeUid);
        Assert.Empty(source.GetPath(new(50,0,0),Vector3.UnitX,new([],"r",RouteAvailability.None,default),RouteAvailability.None));
        Assert.NotEmpty(source.GetPath(new(50,0,0),-Vector3.UnitX,new([],"r",RouteAvailability.None,default),RouteAvailability.None));
    }
}
