using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class JunctionStructureTests
{
    [Fact]
    public void MinorInterfaceWidthsFollowThePlacedPrefabsOriginRotation()
    {
        var descriptor=new PrefabDescriptor{Nodes=[
            new(){Position=Vector3.Zero,InputLines=[0,1,2],OutputLines=[]},
            new(){Position=new(0,0,100),InputLines=[],OutputLines=[0,1]},
            new(){Position=new(100,0,50),InputLines=[],OutputLines=[2]}],
            NavCurves=[new(){StartPosition=new(-1.5f,0,0),EndPosition=new(-1.5f,0,100),Length=100,StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity},
                new(){StartPosition=new(1.5f,0,0),EndPosition=new(1.5f,0,100),Length=100,StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity},
                new(){StartPosition=Vector3.Zero,EndPosition=new(100,0,50),Length=112,StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity}]};
        var prefab=new Prefab{Nodes=[new Node{Uid=10,Position=Vector3.Zero},new Node{Uid=20,Position=new(0,0,100)},
            new Node{Uid=30,Position=new(100,0,50)}]};
        prefab.ChangeOrigin(1);
        var geometry=PrefabPathGeometry.Build(prefab,descriptor);
        Assert.True(geometry.Single(e=>e.StartNodeUid==10&&e.EndNodeUid==20).IsMinorStraight);
        Assert.False(geometry.Single(e=>e.StartNodeUid==10&&e.EndNodeUid==30).IsMinorStraight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SilentUnlitMainRoadRetainsRampStraightStructureAndItsRealEnd(bool minor)
    {
        var info=new RoadForkInfo(10,2,[new(20,ForkSignal.None,[0,1],[0,1],[],true),
            new(30,ForkSignal.Right,[1],[1],[],true)]);
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad main=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,150)])
        {Junction=new(1,2,0){InterfaceCount=3},ForkInfo=info,IsMinorStraight=minor};
        DirectedRoad side=new("prefab:1:0:2",10,30,[new(0,0,100),new(100,0,150)]){ForkInfo=info};
        var detector=new NavigationManeuverDetector();
        var straight=Assert.Single(detector.FindSequence([approach,main],0,[approach,main,side]));
        Assert.Equal(ManeuverKind.Straight,straight.Kind);
        Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
        Assert.Equal(minor,straight.IsMinorStraight);
        Assert.Equal(100,straight.DistanceMeters);Assert.Equal(150,straight.EndDistanceMeters);
        Assert.True(straight.IsMainRoadContinuation);
        var turn=Assert.Single(detector.FindSequence([approach,side],0,[approach,main,side]));
        Assert.Equal(ManeuverKind.KeepRight,turn.Kind);
    }

    [Fact]
    public void SingleMainContinuationOfMergeIsRetainedWithoutInventingMergeAction()
    {
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad main=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,150)])
        {Junction=new(2,1,0){InterfaceCount=3},NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ};
        var straight=Assert.Single(new NavigationManeuverDetector().FindSequence([approach,main],0,[approach,main]));
        Assert.Equal(ManeuverKind.Straight,straight.Kind);
        Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
        Assert.Equal(150,straight.EndDistanceMeters);
    }

    [Fact]
    public void DefaultManeuverRemainsOrdinaryAndNotMinor()
    {
        var action=new NavigationManeuver("test",ManeuverKind.Straight,100,"in","out");
        Assert.Equal(NavigationJunctionKind.Ordinary,action.JunctionKind);
        Assert.False(action.IsMinorStraight);
    }
}
