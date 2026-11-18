using System.Numerics;
using System.Reflection;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
// Broken reverse inputs test explicit rejection, not real-game reachability.
public sealed class ComponentRestoreOwnershipTests
{
    private static readonly DirectedRoad Edge=new("prefab:2A:1:2",1,2,[Vector3.Zero,new(0,0,100)]);
    private static ComponentBoundary P(double offset)=>new(Edge.Id,offset,new(0,0,(float)offset)){StartNodeUid=2,EndNodeUid=1};
    private static ComponentLaneOutcome Lane(int order,double midpoint,ComponentTurn? turn)=>new(order,order,
        ComponentLaneFate.Dies,true,20,P(midpoint),P(100),P(20),P(100),P(20),turn);
    private static ComponentPassageContinuity Fact(ComponentContinuityKind kind,ComponentLaneOutcome[] lanes)
        =>new(Edge.Id,42,2,1,false,kind,P(40),P(100),null,null,[0],[0],2,2,2,null,lanes);
    private static ComponentPassageContinuity Restore(ComponentPassageContinuity fact)
        =>(ComponentPassageContinuity)typeof(ComponentContinuityIndex).GetMethod("Restore",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[fact,new Dictionary<NativeRoadKey,DirectedRoad>{{NativeRoadKey.Of(Edge),Edge}},
                new Dictionary<ulong,LaneContinuityResult>(),new Dictionary<(ulong,ulong),int>()])!;
    [Fact]
    public void MissingActionOwnerCannotUseTheFirstLane()
    {
        var fact=Fact(ComponentContinuityKind.Hard,[Lane(0,10,new(NavigationSide.Left,ComponentTurnKind.Forward))]);
        var wrapper=Assert.Throws<TargetInvocationException>(()=>Restore(fact));
        var error=Assert.IsType<ArgumentException>(wrapper.InnerException);
        Assert.Contains("no lane owning",error.Message);Assert.Contains(Edge.Id,error.Message);
    }
    [Theory][InlineData(ComponentContinuityKind.Main)][InlineData(ComponentContinuityKind.Natural)]
    public void ContinuingActionOwnerIsItsExactInitialMidpoint(ComponentContinuityKind kind)
    {
        var fact=Fact(kind,[Lane(0,20,null),Lane(1,40,new(NavigationSide.Right,ComponentTurnKind.Forward))]);
        Assert.Equal(NavigationSide.Right,Restore(fact).Turn!.Side);
    }
    [Theory][InlineData(ComponentContinuityKind.Unknown)][InlineData(ComponentContinuityKind.Singular)]
    public void FactsWithoutAnActionHaveNoActionOwnerToRestore(ComponentContinuityKind kind)
    {
        var result=Restore(Fact(kind,[]) with{ActionPoint=null});
        Assert.Null(result.ActionPoint);Assert.Null(result.Turn);
    }
}
