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
        Assert.False(straight.IsMinorStraight);
        Assert.Equal(100,straight.DistanceMeters);Assert.Equal(150,straight.EndDistanceMeters);
        Assert.True(straight.IsMainRoadContinuation);
        var turn=Assert.Single(detector.FindSequence([approach,side],0,[approach,main,side]));
        Assert.Equal(ManeuverKind.KeepRight,turn.Kind);
    }

    [Fact]
    public void SingleExitMainContinuationIsNotCountedAsAJunction()
    {
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad main=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,150)])
        {Junction=new(2,1,0){InterfaceCount=3},NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ};
        Assert.Empty(new NavigationManeuverDetector().FindSequence([approach,main],0,[approach,main]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleExitCurvedContinuationDoesNotInventAJunction(bool nativeTangents)
    {
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad main=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,120),new(20,0,140),new(50,0,150)])
        {Junction=new(2,1,0){InterfaceCount=3},
            NativeEntryTangent=nativeTangents?Vector3.UnitZ:null,
            NativeExitTangent=nativeTangents?Vector3.UnitX:null};
        Assert.Empty(new NavigationManeuverDetector().FindSequence([approach,main],0,[approach,main]));
    }

    [Fact]
    public void DefaultManeuverRemainsOrdinaryAndNotMinor()
    {
        var action=new NavigationManeuver("test",ManeuverKind.Straight,100,"in","out");
        Assert.Equal(NavigationJunctionKind.Ordinary,action.JunctionKind);
        Assert.False(action.IsMinorStraight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OppositeDirectionExitDoesNotTurnASingleReachableExitIntoAJunction(bool reverse)
    {
        var info=new PrefabJunctionInfo(2,2,0){InterfaceCount=3};
        DirectedRoad forward=new("prefab:1:0:2",10,20,[new(0,0,100),new(0,0,150)]){Junction=info};
        DirectedRoad backward=new("prefab:1:1:0",30,10,[new(0,0,150),new(0,0,100)]){Junction=info};
        var selected=reverse?backward:forward;
        DirectedRoad approach=new("road",1,selected.StartNodeUid,[new(0,0,0),selected.Points[0]]);
        var actions=new NavigationManeuverDetector().FindSequence([approach,selected],0,[approach,forward,backward]);
        Assert.DoesNotContain(actions,a=>a.Kind==ManeuverKind.Straight);
    }

    [Fact]
    public void SeveralParallelLanePathsToOneInterfaceAreStillOneExit()
    {
        var info=new PrefabJunctionInfo(2,2,0){InterfaceCount=3};
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad first=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,150)]){Junction=info};
        var second=first with{Id="prefab:1:2:3",Points=[new(3,0,100),new(3,0,150)]};
        Assert.DoesNotContain(new NavigationManeuverDetector().FindSequence([approach,first],0,[approach,first,second]),
            a=>a.Kind==ManeuverKind.Straight);
    }

    [Fact]
    public void OtherApproachOfTheSamePrefabStillCountsWhenItHasTwoReachableExits()
    {
        var info=new PrefabJunctionInfo(2,2,0){InterfaceCount=4};
        DirectedRoad single=new("prefab:1:0:2",10,20,[new(0,0,100),new(0,0,150)]){Junction=info};
        var fork=new RoadForkInfo(30,1,[new(20,ForkSignal.None,[0],[0],[],true),new(40,ForkSignal.Right,[0],[0],[],true)]);
        DirectedRoad chosen=new("prefab:1:1:2",30,20,[new(0,0,100),new(0,0,150)]){Junction=info,ForkInfo=fork};
        DirectedRoad branch=new("prefab:1:1:3",30,40,[new(0,0,100),new(50,0,150)]){Junction=info,ForkInfo=fork};
        DirectedRoad approach=new("road",1,30,[Vector3.Zero,new(0,0,100)]);
        var straight=Assert.Single(new NavigationManeuverDetector().FindSequence([approach,chosen],0,[approach,single,chosen,branch]));
        Assert.Equal(ManeuverKind.Straight,straight.Kind);Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
    }

    [Fact]
    public void CrossingJunctionIsNotNarrowedByTheRampExitRule()
    {
        DirectedRoad approach=new("road",1,10,[Vector3.Zero,new(0,0,100)]);
        DirectedRoad straight=new("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,150)])
            {Junction=new(2,2,1){InterfaceCount=3}};
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([approach,straight],0,[approach,straight]));
        Assert.Equal(ManeuverKind.Straight,action.Kind);Assert.Equal(NavigationJunctionKind.Ordinary,action.JunctionKind);
    }
}
