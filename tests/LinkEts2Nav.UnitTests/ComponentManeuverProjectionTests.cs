using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class ComponentManeuverProjectionTests
{
    private static DirectedRoad Edge(string id,float start=0,float end=100)=>new(id,1,2,[new(0,0,start),new(0,0,end)]);
    private static ComponentBoundary At(DirectedRoad edge,double distance,Vector3 position)=>new(edge.Id,distance,position)
        {StartNodeUid=edge.StartNodeUid,EndNodeUid=edge.EndNodeUid};
    private static ComponentPassageContinuity Fact(DirectedRoad edge,ComponentContinuityKind kind,
        ComponentTurn? turn=null,bool main=false)=>new(edge.Id,42,edge.StartNodeUid,edge.EndNodeUid,main,kind,
            At(edge,50,new(0,0,50)),At(edge,100,new(0,0,100)),null,null,[0],[0],2,3,1,turn,[]);

    [Fact]
    public void SelectedLocalDeadLaneIsNotHiddenBySurvivingMainEntranceGroup()
    {
        var edge=Edge("prefab:2A:1:2");var turn=new ComponentTurn(NavigationSide.Left,ComponentTurnKind.Forward);
        var lane=new ComponentLaneOutcome(1,0,ComponentLaneFate.Dies,true,-20,
            At(edge,50,new(0,0,50)),At(edge,100,new(0,0,100)),At(edge,80,new(0,0,80)),
            At(edge,100,new(0,0,100)),null,turn){StructuralConstraintId="native-merge:42:3"};
        var fact=Fact(edge,ComponentContinuityKind.Main,main:true) with{Lanes=[lane]};
        var selected=new PrefabLanePath(42,1,2,1,0,1,0,2,1,[1,3],edge.Points,100);
        var action=Assert.Single(ComponentManeuverProjection.Build([edge],0,[fact],[],selected).Actions);
        Assert.Equal(ManeuverKind.KeepLeft,action.Kind);Assert.Equal(80,action.DistanceMeters);
        Assert.Equal(100,action.EndDistanceMeters);Assert.False(action.IsMainRoadContinuation);
    }
    [Fact]
    public void HardEntryUsesPrimitiveTurnAndOppositeTrafficSide()
    {
        var edge=Edge("prefab:2A:1:2");var fact=Fact(edge,ComponentContinuityKind.Hard,new(NavigationSide.Right,ComponentTurnKind.Turn));
        var result=ComponentManeuverProjection.Build([edge],0,[fact],[]);
        var action=Assert.Single(result.Actions);
        Assert.Equal(ManeuverKind.TurnRight,action.Kind);
        Assert.Equal("右转，注意左侧来车",action.ExecutionInstruction);
        Assert.Equal(100,action.EndDistanceMeters);
    }
    [Fact]
    public void SoftEntryUsesForwardWordsAndSeparatePhysicalLifetime()
    {
        var first=Edge("prefab:2A:1:2");var later=Edge("prefab:2B:2:3",100,500);
        var fact=Fact(first,ComponentContinuityKind.Soft,new(NavigationSide.Left,ComponentTurnKind.Back)) with
        {ActionPoint=At(later,50,new(0,0,150)),IntervalStart=At(first,100,new(0,0,100)),
            IntervalEnd=At(later,250,new(0,0,350)),LifecycleEnd=At(later,400,new(0,0,500))};
        var action=Assert.Single(ComponentManeuverProjection.Build([first,later],0,[fact],[]).Actions);
        Assert.Equal(ManeuverKind.KeepLeft,action.Kind);
        Assert.Equal("向左前方行驶",action.Instruction);
        Assert.Equal(150,action.DistanceMeters);
        Assert.Equal(500,action.EndDistanceMeters);
        Assert.Equal(new LaneActionInterval(100,350),action.ApproachInterval);
    }
    [Fact]
    public void SoftExitMapsItsUpstreamApproachInsteadOfSearchingOnlyAfterTheFork()
    {
        var approach=Edge("road:1");var fork=Edge("prefab:2A:1:2",100,200);
        var fact=Fact(fork,ComponentContinuityKind.Soft,new(NavigationSide.Right,ComponentTurnKind.Turn)) with
        {ActionPoint=At(approach,70,new(0,0,70)),IntervalStart=At(approach,0,Vector3.Zero),
            IntervalEnd=At(fork,0,new(0,0,100))};
        var action=Assert.Single(ComponentManeuverProjection.Build([approach,fork],1,[],[fact]).Actions);
        Assert.Equal(70,action.DistanceMeters);
        Assert.Equal(200,action.EndDistanceMeters);
    }
    [Fact]
    public void RepeatedEdgeBindsTheCurrentVisitRatherThanTheFirstUidOccurrence()
    {
        var edge=Edge("prefab:2A:1:2");var loop=Edge("road:loop",100,0);
        var fact=Fact(edge,ComponentContinuityKind.Hard,new(NavigationSide.Left,ComponentTurnKind.Forward));
        var action=Assert.Single(ComponentManeuverProjection.Build([edge,loop,edge],2,[fact],[]).Actions);
        Assert.Equal(250,action.DistanceMeters);Assert.Equal(300,action.EndDistanceMeters);
    }
    [Fact]
    public void NaturalEntrySelectsDownstreamLanesWhileNaturalExitSelectsUpstreamLanes()
    {
        var edge=Edge("prefab:2A:1:2");
        var entry=Fact(edge,ComponentContinuityKind.Natural) with{ExitOrders=[2]};
        Assert.Equal("走最右侧车道",Assert.Single(ComponentManeuverProjection.Build([edge],0,[entry],[]).Actions).Instruction);
        var exit=entry with{EntryLaneCount=4,EntryOrders=[0,1],LegalExitCount=2,ExitTurnRadians=-.1};
        var other=exit with{ExitNodeUid=3,EntryOrders=[2,3],ExitTurnRadians=.1};
        var result=ComponentManeuverProjection.Build([edge],0,[],[exit,other]);
        Assert.Contains(result.Actions,a=>a.Instruction=="分岔口靠左，走左侧两车道");
    }
    [Fact]
    public void WholeRoadNaturalContinuationDoesNotInventAnAllLanesInstruction()
    {
        var edge=Edge("prefab:2A:1:2");var fact=Fact(edge,ComponentContinuityKind.Natural) with{ExitOrders=[0,1,2]};
        var action=Assert.Single(ComponentManeuverProjection.Build([edge],0,[fact],[]).Actions);
        Assert.Equal(ManeuverKind.Straight,action.Kind);Assert.True(action.IsMainRoadContinuation);Assert.Null(action.Instruction);
    }
    [Fact]
    public void CollinearUnknownSideIsDiagnosedWithoutInventingATurnOrCallingItSingular()
    {
        var edge=Edge("prefab:2A:1:2");var fact=Fact(edge,ComponentContinuityKind.Hard,new(NavigationSide.None,ComponentTurnKind.Unknown));
        var result=ComponentManeuverProjection.Build([edge],0,[fact],[]);
        Assert.Empty(result.Actions);Assert.Single(result.Issues);Assert.Single(result.Unresolved);
    }
    [Fact]
    public void OppositeDirectionOfTheSameRoadIdCannotSupplyAnApproachBoundary()
    {
        var reverse=Edge("road:1") with{StartNodeUid=2,EndNodeUid=1};var fork=Edge("prefab:2A:1:2",100,200);
        var forward=reverse with{StartNodeUid=1,EndNodeUid=2};
        var fact=Fact(fork,ComponentContinuityKind.Soft,new(NavigationSide.Left,ComponentTurnKind.Forward)) with
        {ActionPoint=At(forward,70,new(0,0,70)),IntervalStart=At(forward,0,Vector3.Zero),IntervalEnd=At(fork,0,new(0,0,100))};
        var result=ComponentManeuverProjection.Build([reverse,fork],1,[],[fact]);
        Assert.Empty(result.Actions);Assert.Single(result.Unresolved);
    }
    [Fact]
    public void SharedConstraintBecomesOneSeriesButDistinctConstraintsInsideOnePrefabRemain()
    {
        var edge=Edge("prefab:2A:1:2");var first=Fact(edge,ComponentContinuityKind.Hard,new(NavigationSide.Left,ComponentTurnKind.Forward)) with{StructuralConstraintId="merge:2A:7"};
        var duplicate=first with{ActionPoint=At(edge,60,new(0,0,60))};
        var single=ComponentManeuverProjection.Build([edge],0,[first],[duplicate]);
        Assert.Single(single.Actions);Assert.Equal(50,single.Actions[0].DistanceMeters);
        var next=duplicate with{StructuralConstraintId="split:2A:9"};
        Assert.Equal(2,ComponentManeuverProjection.Build([edge],0,[first],[next]).Actions.Count);
    }
    [Fact]
    public void SameStaticSoftEntranceIsRequiredOnlyWhenTheRouteLeavesItsSideOutlet()
    {
        var initial=Edge("prefab:2A:1:2");var road=Edge("road:1",100,200) with{StartNodeUid=2,EndNodeUid=3};
        var side=Edge("prefab:2B:3:4",200,300) with{StartNodeUid=3,EndNodeUid=4};
        var main=side with{Id="prefab:2B:3:5",EndNodeUid=5};
        var terminal=At(side,0,new(0,0,200));
        var lane=new ComponentLaneOutcome(0,0,ComponentLaneFate.Separates,false,100,
            At(initial,50,new(0,0,50)),At(initial,100,new(0,0,100)),terminal,terminal,At(road,0,new(0,0,100)),new(NavigationSide.Left,ComponentTurnKind.Forward));
        var fact=Fact(initial,ComponentContinuityKind.Soft,new(NavigationSide.Left,ComponentTurnKind.Forward)) with
        {ActionPoint=lane.SoftAction,LifecycleEnd=terminal,IntervalStart=lane.InitialExit,IntervalEnd=terminal,Lanes=[lane]};
        Assert.Empty(ComponentManeuverProjection.Build([initial,road,side],0,[fact],[]).Actions);
        // The boundary node is common to both outgoing passages, but its typed
        // identity still names the static side outlet. Main selection requires
        // the same physical entry point, not pretending the side path was removed.
        Assert.Single(ComponentManeuverProjection.Build([initial,road,main],0,[fact],[]).Actions);
        var loop=road with{Id="road:return",StartNodeUid=5,EndNodeUid=3,Points=[new(0,0,300),new(0,0,200)]};
        var laterSide=Assert.Single(ComponentManeuverProjection.Build([initial,road,main,loop,side],0,[fact],[]).Actions);
        Assert.Equal(200,laterSide.EndDistanceMeters); // first terminal visit, not the later matching side edge
        Assert.Equal(ComponentContinuityKind.Soft,fact.Kind);
    }
}
