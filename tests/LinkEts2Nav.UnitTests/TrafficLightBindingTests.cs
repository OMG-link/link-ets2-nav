using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using ScsMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightBindingTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static PrefabLanePath Lane(ulong entry,ulong exit,int curve=0,int lane=0)=>new(7,entry,exit,lane,0,lane,0,2,1,[curve],
        [new(100,0,lane*3.5f),new(130,0,lane*3.5f)],30);
    private static TrafficSignalControl Control(int curve=0,int signal=0,float z=0)=>new(7,curve,signal,new(100,0,z),Vector3.UnitX,
        [new(new(100,0,4+z),Quaternion.Identity)],new(SignalPhase.Green,"PPD"),"known");
    private static GameDataSnapshot Data(bool navigation=true,float x=90,float z=0)=>new(
        new(x,0,z,.75,0,100,true,false,1,Now),RouteSnapshot.Parse(navigation?[new(1,100,1),new(2,0,0)]:[],Now),
        navigation?RouteAvailability.Available:RouteAvailability.None,"s",true)
        {TrafficSignals=new(Now,[new(99,new(100,0,4),Quaternion.Identity,SignalPhase.Red,2)])};
    private static NearbyMapSnapshot Nearby(IEnumerable<TrafficSignalControl> controls,ScsMap? map=null,IReadOnlyList<DirectedRoad>? paths=null)
        =>new(map??new(),[],[],[]){TrafficSignals=new(controls),PrefabPaths=paths??[]};
    private static NavigationDiagnostic Navigation(params PrefabLanePath[] paths)=>new("test")
        {LaneRoute=new(paths.Select((p,i)=>new LaneRouteSection(i.ToString(),p)).ToArray(),[],true,null)};

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
    public void UnknownStaticTransitionAndOppositelyFacingLampsAreNotPredicted()
    {
        var binder=new TrafficSignalBinder();
        Assert.Null(binder.Match(Data(),Nearby([Control() with{Timing=null}]),Navigation(Lane(1,2)),out _));
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
        DirectedRoad Edge(PrefabLanePath lane)=>new("prefab:7:"+lane.ExitNodeUid,entry,lane.ExitNodeUid,lane.Points){LanePaths=[lane]};
        var binder=new TrafficSignalBinder();
        var shared=Nearby([Control(),Control(1,z:3.5f)],map,[Edge(through),Edge(turn)]);
        Assert.NotNull(binder.Match(Data(false),shared,Navigation(),out _));
        var split=Nearby([Control(),Control(1,1,3.5f)],map,[Edge(through),Edge(turn)]);
        Assert.Null(new TrafficSignalBinder().Match(Data(false),split,Navigation(),out var reason));
        Assert.Contains("不共享",reason);
        // A nearby lamp is insufficient when there is no proven road approach at all.
        Assert.Null(new TrafficSignalBinder().Match(Data(false),Nearby([Control()],paths:[Edge(through)]),Navigation(),out _));
    }
}
