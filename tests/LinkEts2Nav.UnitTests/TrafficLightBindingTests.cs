using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using ScsMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightBindingTests
{
    [Fact]
    public void ControlledGroupStillRequiresAMatchingConsistentLiveLamp()
    {
        var control=Control();
        var nearby=Nearby([control]);var navigation=Navigation(Lane(1,2));
        Assert.NotNull(new TrafficSignalBinder().Match(Data(),nearby,navigation,out _));
        Assert.Null(new TrafficSignalBinder().Match(Data() with{TrafficSignals=new(Now,[])},nearby,navigation,out _));
        var inconsistent=Data() with{TrafficSignals=new(Now,[new(1,new(100,0,4),Quaternion.Identity,SignalPhase.Red,2),
            new(2,new(100,0,4),Quaternion.Identity,SignalPhase.Green,2)])};
        Assert.Null(new TrafficSignalBinder().Match(inconsistent,nearby,navigation,out _));
        Assert.Null(new TrafficSignalBinder().Match(Data(),Nearby([control,Control(1,1)]),Navigation(Lane(1,2),Lane(1,3,1)),out _));
    }
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static PrefabLanePath Lane(ulong entry,ulong exit,int curve=0,int lane=0)=>new(7,entry,exit,lane,0,lane,0,2,1,[curve],
        [new(100,0,lane*3.5f),new(130,0,lane*3.5f)],30);
    private static DirectedRoad Edge(PrefabLanePath lane) =>
        new($"prefab:7:{lane.ExitNodeUid}", lane.EntryNodeUid, lane.ExitNodeUid, lane.Points)
        { LanePaths = [lane], NativeEntryTangent = Vector3.UnitX, NativeExitTangent = Vector3.UnitX };
    private static TrafficSignalControl Control(int curve=0,int signal=0,float z=0)=>new(7,curve,signal,new(100,0,z),Vector3.UnitX,
        [new(new(100,0,4+z),Quaternion.Identity)]);
    private static GameDataSnapshot Data(bool navigation=true,float x=90,float z=0)=>new(
        new(x,0,z,.75,0,100,true,false,1,Now),RouteSnapshot.Parse(navigation?[new(1,100,1),new(2,0,0)]:[],Now),
        navigation?RouteAvailability.Available:RouteAvailability.None,"s",true)
        {TrafficSignals=new(Now,[new(99,new(100,0,4),Quaternion.Identity,SignalPhase.Red,2)])};
    private static NearbyMapSnapshot Nearby(IEnumerable<TrafficSignalControl> controls,ScsMap? map=null,IReadOnlyList<DirectedRoad>? paths=null)
        =>new(map??new(),[],[],[]){TrafficSignals=new(controls),PrefabPaths=paths??[]};
    private static NavigationDiagnostic Navigation(params PrefabLanePath[] paths)=>new("test")
        {LaneRoute=new(paths.Select((p,i)=>new LaneRouteSection(i.ToString(),p,i)).ToArray(),[],true,null)};

    [Fact]
    public void NavigationBindsOnlyItsChosenCurveGroupInsteadOfTheNearestLamp()
    {
        var binder=new TrafficSignalBinder();
        var selected=binder.Match(Data(),Nearby([Control(),Control(1,1)]),Navigation(Lane(1,2)),out _);
        Assert.NotNull(selected);Assert.Equal(0,selected.Control.SignalId);Assert.Equal(10,selected.StopDistance);
        Assert.Null(binder.Match(Data(),Nearby([Control(1,1)]),Navigation(Lane(1,2)),out _));
    }
    [Theory]
    [InlineData(74,0)] // A stopped traffic jam over 25 m before the line.
    [InlineData(101,0)] // Already beyond the stop line.
    [InlineData(90,3)] // A different lane cannot borrow this controlled curve.
    public void WaitingMustBeImmediatelyBeforeTheSelectedStopLine(float x,float z)
        =>Assert.Null(new TrafficSignalBinder().Match(Data(x:x,z:z),Nearby([Control()]),Navigation(Lane(1,2)),out _));

    [Fact]
    public void OppositelyFacingLampsCannotBindToTheSelectedGroup()
    {
        var binder=new TrafficSignalBinder();
        var data=Data() with{TrafficSignals=new(Now,[new(99,new(100,0,4),Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI),SignalPhase.Red,2)])};
        Assert.Null(binder.Match(data,Nearby([Control()]),Navigation(Lane(1,2)),out _));
    }
    [Fact]
    public void DuplicatedSameGroupLampsMustAgreeOnPhaseAndRemainingTime()
    {
        var data=Data() with{TrafficSignals=new(Now,[new(1,new(100,0,4),Quaternion.Identity,SignalPhase.Red,2),
            new(2,new(100,0,4),Quaternion.Identity,SignalPhase.Green,2)])};
        Assert.Null(new TrafficSignalBinder().Match(data,Nearby([Control()]),Navigation(Lane(1,2)),out _));
    }
    [Fact]
    public void NoNavigationRequiresEveryLegalContinuationToUseOneGroup()
    {
        var map=new ScsMap();var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        ulong entry=road.ForwardNode.Uid;
        var through=Lane(entry,2);var turn=Lane(entry,3,1,1);
        var binder=new TrafficSignalBinder();
        var shared=Nearby([Control(),Control(1,z:3.5f)],map,[Edge(through),Edge(turn)]);
        Assert.NotNull(binder.Match(Data(false),shared,Navigation(),out _));
        var split=Nearby([Control(),Control(1,1,3.5f)],map,[Edge(through),Edge(turn)]);
        Assert.Null(new TrafficSignalBinder().Match(Data(false),split,Navigation(),out var reason));
        Assert.Contains("不共享",reason);
        // A nearby lamp is insufficient when there is no proven road approach at all.
        Assert.Null(new TrafficSignalBinder().Match(Data(false),Nearby([Control()],paths:[Edge(through)]),Navigation(),out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalUnroutedReturnDoesNotChangeTheForwardStopGroup(bool returnHasDifferentGroup)
    {
        var map=new ScsMap();var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        ulong entry=road.ForwardNode.Uid;
        var forward=Lane(entry,2);var returning=Lane(entry,entry,1);
        var controls=returnHasDifferentGroup?new[]{Control(),Control(1,1)}:new[]{Control()};
        var selected=new TrafficSignalBinder().Match(Data(false),Nearby(controls,map,[Edge(forward),Edge(returning)]),Navigation(),out _);
        Assert.NotNull(selected);Assert.Equal(0,selected.Control.SignalId);
    }

    [Fact]
    public void NavigationExplicitlySelectingAReturnKeepsItsOwnControlledGroup()
    {
        var returning=Lane(1,1,1);
        var edge=new DirectedRoad("prefab:7:0:0",1,1,returning.Points){LanePaths=[returning]};
        var selected=new TrafficSignalBinder().Match(Data(),Nearby([Control(),Control(1,1)],paths:[edge]),Navigation(returning),out _);
        Assert.NotNull(selected);Assert.Equal(1,selected.Control.SignalId);
    }
}
