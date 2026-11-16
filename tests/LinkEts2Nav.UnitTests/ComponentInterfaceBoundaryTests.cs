using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class ComponentInterfaceBoundaryTests
{
    private static DirectedRoad Edge(string id,ulong start,ulong end,float length)=>new(id,start,end,[Vector3.Zero,new(0,0,length)]);
    private static readonly DirectedRoad Side=Edge("prefab:2A:1:3",1,3,100);
    private static readonly DirectedRoad Main=Edge("prefab:2A:2:3",2,3,120);
    private static readonly DirectedRoad Fork=Edge("prefab:2B:3:4",3,4,50);
    private static ComponentBoundary Point(DirectedRoad e,double at,ComponentBoundaryInterface endpoint=ComponentBoundaryInterface.None)
        =>new(e.Id,at,new(0,0,(float)at)){StartNodeUid=e.StartNodeUid,EndNodeUid=e.EndNodeUid,Interface=endpoint};
    private static ComponentPassageContinuity Fact(ComponentBoundary start)=>new(Fork.Id,43,3,4,false,ComponentContinuityKind.Soft,
        start,Point(Fork,50,ComponentBoundaryInterface.End),start,Point(Fork,0,ComponentBoundaryInterface.Start),
        [2],[0],3,1,2,new(NavigationSide.Right,ComponentTurnKind.Forward),[]);
    [Fact]
    public void CommonOutputInterfaceMapsAcrossDifferentEntrancePassages()
    {
        var boundary=Point(Side,100,ComponentBoundaryInterface.End);
        var result=ComponentManeuverProjection.Build([Main,Fork],1,[],[Fact(boundary)]);
        var action=Assert.Single(result.Actions);
        Assert.Equal(ManeuverKind.KeepRight,action.Kind);Assert.Equal(120,action.DistanceMeters);
        Assert.Equal(170,action.EndDistanceMeters);Assert.Equal(new LaneActionInterval(120,120),action.ApproachInterval);
        Assert.Empty(result.Issues);
    }
    [Fact]
    public void InternalPointDoesNotAliasJustBecauseItsPassageHasTheSameExit()
    {
        var result=ComponentManeuverProjection.Build([Main,Fork],1,[],[Fact(Point(Side,50))]);
        Assert.Empty(result.Actions);Assert.NotEmpty(result.Issues);
    }
    [Fact]
    public void OppositeDirectionDoesNotAliasAnExitToItsEntrance()
    {
        var reverse=Main with{StartNodeUid=3,EndNodeUid=2};
        var result=ComponentManeuverProjection.Build([reverse,Fork],1,[],[Fact(Point(Side,100,ComponentBoundaryInterface.End))]);
        Assert.Empty(result.Actions);Assert.NotEmpty(result.Issues);
    }
    [Fact]
    public void RepeatedInterfaceUsesThisVisitInsteadOfTheEarlierOccurrence()
    {
        var back=Edge("return",4,2,30);
        var result=ComponentManeuverProjection.Build([Main,Fork,back,Main,Fork],4,[],[Fact(Point(Side,100,ComponentBoundaryInterface.End))]);
        var action=Assert.Single(result.Actions);Assert.Equal(320,action.DistanceMeters);Assert.Equal(370,action.EndDistanceMeters);
    }
    [Fact]
    public void ExactDirectedPassageRetainsAnInternalPoint()
    {
        var result=ComponentManeuverProjection.Build([Side,Fork],1,[],[Fact(Point(Side,50))]);
        var action=Assert.Single(result.Actions);Assert.Equal(50,action.DistanceMeters);Assert.Equal(150,action.EndDistanceMeters);
    }
    [Fact]
    public void UnknownZeroInterfaceUidCannotAliasAnotherUnknownEndpoint()
    {
        var boundary=Point(Side with{EndNodeUid=0},100,ComponentBoundaryInterface.End);
        Assert.Null(boundary.InterfaceNodeUid);
        var result=ComponentManeuverProjection.Build([Main with{EndNodeUid=0},Fork],1,[],[Fact(boundary)]);
        Assert.Empty(result.Actions);Assert.NotEmpty(result.Issues);
    }
}
