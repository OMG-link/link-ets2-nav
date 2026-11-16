using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class ComponentManeuverProjectionTests
{
    private static DirectedRoad Edge(string id,float start=0,float end=100)=>new(id,1,2,[new(0,0,start),new(0,0,end)]);
    private static ComponentPassageContinuity Fact(DirectedRoad edge,ComponentContinuityKind kind,
        ComponentTurn? turn=null,bool main=false)=>new(edge.Id,42,edge.StartNodeUid,edge.EndNodeUid,main,kind,
            new(edge.Id,50,new(0,0,50)),new(edge.Id,100,new(0,0,100)),null,null,[0],[0],2,3,1,turn,[]);

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
        {ActionPoint=new(later.Id,50,new(0,0,150)),IntervalStart=new(first.Id,100,new(0,0,100)),
            IntervalEnd=new(later.Id,250,new(0,0,350)),LifecycleEnd=new(later.Id,400,new(0,0,500))};
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
        {ActionPoint=new(approach.Id,70,new(0,0,70)),IntervalStart=new(approach.Id,0,Vector3.Zero),
            IntervalEnd=new(fork.Id,0,new(0,0,100))};
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
}
