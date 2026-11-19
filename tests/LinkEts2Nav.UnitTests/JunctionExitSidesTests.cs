using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class JunctionExitSidesTests
{
    private static ComponentPassageContinuity Exit(ulong node,ComponentContinuityKind kind,int[] orders,
        NavigationSide side=NavigationSide.None,ulong entry=10,ulong prefab=42)
        =>new($"prefab:{prefab:X16}:0:{node}",prefab,entry,node,kind==ComponentContinuityKind.Main,kind,
            null,null,null,null,orders,[],4,2,2,
            side==NavigationSide.None?null:new(side,ComponentTurnKind.Forward),[]);

    [Theory]
    [InlineData(ComponentContinuityKind.Hard)]
    [InlineData(ComponentContinuityKind.Soft)]
    public void HardAndSoftUseEstablishedTurnRatherThanLaneOrder(ComponentContinuityKind kind)
    {
        var main=Exit(20,ComponentContinuityKind.Main,[1,2]);
        // Lane orders deliberately suggest left. Only the established Turn is authoritative.
        var other=Exit(30,kind,[0],NavigationSide.Right);
        Assert.Equal(NavigationSide.Right,JunctionExitSides.OtherSides([main,other],42,10,20));
    }

    [Theory]
    [InlineData(ComponentContinuityKind.Main)]
    [InlineData(ComponentContinuityKind.Natural)]
    public void NaturalOutletsCompareInheritedLaneGroupsAndIgnoreTurn(ComponentContinuityKind selectedKind)
    {
        var selected=Exit(20,selectedKind,[1,2]);
        var left=Exit(30,ComponentContinuityKind.Natural,[0],NavigationSide.Right);
        var right=Exit(40,ComponentContinuityKind.Natural,[3],NavigationSide.Left);
        Assert.Equal(NavigationSide.Left,JunctionExitSides.OtherSides([selected,left],42,10,20));
        Assert.Equal(NavigationSide.Right,JunctionExitSides.OtherSides([selected,right],42,10,20));
        Assert.Equal(NavigationSide.Left|NavigationSide.Right,
            JunctionExitSides.OtherSides([selected,left,right],42,10,20));
    }

    [Fact]
    public void OnlyNativePassagesFromThisEntranceAndPrefabParticipate()
    {
        var selected=Exit(20,ComponentContinuityKind.Main,[0]);
        var legal=Exit(30,ComponentContinuityKind.Natural,[1]);
        var otherEntrance=Exit(40,ComponentContinuityKind.Hard,[0],NavigationSide.Left,entry:11);
        var otherPrefab=Exit(50,ComponentContinuityKind.Hard,[0],NavigationSide.Left,prefab:43);
        Assert.Equal(NavigationSide.Right,
            JunctionExitSides.OtherSides([selected,legal,otherEntrance,otherPrefab],42,10,20));
    }

    [Theory]
    [InlineData(new[]{0,2},new[]{1})]
    [InlineData(new[]{0,1},new[]{1,2})]
    [InlineData(new[]{0,1},new[]{0,1})]
    [InlineData(new[]{0,1},new int[0])]
    public void UnestablishedInheritedOrderingFailsInsteadOfGuessing(int[] selectedOrders,int[] otherOrders)
    {
        var selected=Exit(20,ComponentContinuityKind.Main,selectedOrders);
        var other=Exit(30,ComponentContinuityKind.Natural,otherOrders,NavigationSide.Right);
        Assert.Throws<NotSupportedException>(()=>JunctionExitSides.OtherSides([selected,other],42,10,20));
    }

    [Theory]
    [InlineData(ComponentContinuityKind.Hard)]
    [InlineData(ComponentContinuityKind.Soft)]
    [InlineData(ComponentContinuityKind.Unknown)]
    [InlineData(ComponentContinuityKind.Singular)]
    public void MissingDirectionIsNotSilentlyOmitted(ComponentContinuityKind kind)
    {
        var selected=Exit(20,ComponentContinuityKind.Main,[0]);
        Assert.Throws<NotSupportedException>(()=>JunctionExitSides.OtherSides([selected,Exit(30,kind,[1])],42,10,20));
    }

    [Fact]
    public void TransparentPassageLooksUpOutletFactWhenTheEntryOwnsTheAction()
    {
        var edge=new DirectedRoad("prefab:000000000000002A:0:20",10,20,[Vector3.Zero,new(0,0,100)]);
        var entry=Exit(20,ComponentContinuityKind.Main,[3]) with{EdgeId=edge.Id};
        var selected=Exit(20,ComponentContinuityKind.Main,[0,1]) with{EdgeId=edge.Id};
        var other=Exit(30,ComponentContinuityKind.Soft,[2],NavigationSide.Right);
        // Entry and exit lane identities need not be interchangeable. The
        // entry generates the sole passage; the side proof comes from Exits.
        var action=Assert.Single(ComponentManeuverProjection.Build([edge],0,[entry],[selected,other]).Actions);
        Assert.Equal(ManeuverKind.Straight,action.Kind);
        Assert.Equal(NavigationSide.Right,action.StraightExitSides);
        Assert.Equal(0,action.DistanceMeters);Assert.Equal(100,action.EndDistanceMeters);
    }

#if DEBUG
    [Fact]
    public void SelectedReferenceMustBeAUniqueInheritedOutletFact()
    {
        var selected=Exit(20,ComponentContinuityKind.Main,[0]);
        Assert.Throws<NavigationContractException>(()=>JunctionExitSides.OtherSides([selected],42,10,30));
        Assert.Throws<NavigationContractException>(()=>JunctionExitSides.OtherSides([selected,selected],42,10,20));
        Assert.Throws<NavigationContractException>(()=>JunctionExitSides.OtherSides([selected with{Kind=ComponentContinuityKind.Hard}],42,10,20));
        Assert.Throws<NavigationContractException>(()=>JunctionExitSides.OtherSides([selected with{EntryOrders=[]}],42,10,20));
    }
#endif

    [Fact]
    public void ExistingNaturalChoiceStillOwnsItsAnnouncementInsteadOfAddingPassageSpeech()
    {
        var edge=new DirectedRoad("prefab:000000000000002A:0:20",10,20,[Vector3.Zero,new(0,0,100)]);
        edge=edge with{LanePaths=[new(42,10,20,0,0,0,0,4,2,[0],edge.Points,100),
            new(42,10,20,1,1,1,1,4,2,[1],edge.Points,100)]};
        var start=new ComponentBoundary(edge.Id,50,new(0,0,50)){StartNodeUid=10,EndNodeUid=20};
        var end=start with{OffsetMeters=100,Position=new(0,0,100)};
        var main=Exit(20,ComponentContinuityKind.Main,[0,1]) with
            {EdgeId=edge.Id,HasNaturalAlternative=true,ActionPoint=start,LifecycleEnd=end};
        var natural=Exit(30,ComponentContinuityKind.Natural,[2,3]);
        var action=Assert.Single(ComponentManeuverProjection.Build([edge],0,[],[main,natural]).Actions);
        Assert.Equal(ManeuverKind.ChooseLanes,action.Kind);
        Assert.Equal("分岔口靠左，走左侧两车道",action.Instruction);
    }
}
