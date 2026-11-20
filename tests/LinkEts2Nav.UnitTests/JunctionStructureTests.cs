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
        var graph=SelectedStraightPassageTests.Fork();
        var main=graph[0] with{IsMinorStraight=minor};var side=graph[1];
        DirectedRoad approach=new("road",1,10,[new(0,0,-100),Vector3.Zero]);
        var detector=new NavigationManeuverDetector();
        var straight=Assert.Single(detector.FindSequence([approach,main],0,[approach,main,side]));
        Assert.Equal(ManeuverKind.Straight,straight.Kind);Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
        Assert.False(straight.IsMinorStraight);Assert.True(straight.IsMainRoadContinuation);
        Assert.Equal(100,straight.DistanceMeters);Assert.Equal(200,straight.EndDistanceMeters);
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
        var graph=SelectedStraightPassageTests.Fork();
        var native=graph[0].LanePaths[0].NativeGeometry!;
        var solo=new NativeLaneCurve(99,new(10,0,0),new(0,0,100),new(-10,0,100),new(-10,0,100),[],[]);
        native=NativeLaneFixture.WithInterfaces(42,[..native.Curves,solo]);
        var chosen=graph[0] with{Id="prefab:2A:40:20",StartNodeUid=40,LanePaths=graph[0].LanePaths.Select(p=>p with{EntryNodeUid=40,NativeGeometry=native}).ToArray()};
        var branch=graph[1] with{Id="prefab:2A:40:30",StartNodeUid=40,LanePaths=graph[1].LanePaths.Select(p=>p with{EntryNodeUid=40,NativeGeometry=native}).ToArray()};
        var lane=new PrefabLanePath(42,10,20,0,0,0,0,1,1,[99],[solo.Start,solo.End],101){NativeGeometry=native,CurveStartPointIndices=[0]};
        var single=new DirectedRoad("prefab:2A:10:20",10,20,lane.Points){LanePaths=[lane]};
        var detector=new NavigationManeuverDetector();
        var straight=Assert.Single(detector.FindSequence([chosen],0,[single,chosen,branch]),a=>a.Kind==ManeuverKind.Straight);
        Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
        Assert.DoesNotContain(detector.FindSequence([single],0,[single,chosen,branch]),a=>a.Kind==ManeuverKind.Straight);

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
