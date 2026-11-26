using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneApproachTurnTests
{
    private static NativeLaneCurve C(int id,Vector3 start,Vector3 end,int[] next,int[] previous)
        =>new(id,start,end,end-start,end-start,next,previous);
    private static NativeLaneGeometry Merge(bool backward=false)=>new(42,[
        C(0,new(-100,0,-3),new(-40,0,3),[1],[]) with{StartTangent=new(backward?-100:100,0,0)},
        C(1,new(-40,0,3),Vector3.Zero,[3],[0]) with{StartTangent=new(backward?-40:40,0,0)},
        C(2,new(-100,0,0),Vector3.Zero,[3],[]),C(3,Vector3.Zero,new(100,0,0),[],[1,2])])
        {InputCurveIndices=[0,2],OutputCurveIndices=[3]};
    private static NativeLaneGeometry Reverse(NativeLaneGeometry g)=>new(g.PrefabUid,g.Curves.Select(c=>
        new NativeLaneCurve(c.Index,c.End,c.Start,-c.EndTangent,-c.StartTangent,c.Previous,c.Next)).ToArray())
        {InputCurveIndices=g.OutputCurveIndices,OutputCurveIndices=g.InputCurveIndices};
    private static LaneContinuityPoint Point(NativeLaneGeometry g,bool merge=true)=>Assert.Single(LaneContinuity.Analyze(g).Points,
        p=>p.SharedCurveIndex==3&&p.Kind==(merge?LaneContinuityPointKind.Merge:LaneContinuityPointKind.Split));
    private static LaneContinuityBranch Side(NativeLaneGeometry g,bool merge=true)=>Assert.Single(Point(g,merge).Branches,b=>b.CurveIndex==1);

    [Theory]
    [InlineData(false,LaneTurnKind.ForwardRight)]
    [InlineData(true,LaneTurnKind.RearLeft)]
    public void MergeUsesBothTheFirstStartRayAndItsPosition(bool backward,LaneTurnKind expected)
    {
        var g=Merge(backward);var actual=Side(g);
        Assert.Equal(0,actual.TurnSourceCurveIndex);Assert.Equal(expected,actual.Turn);
        Assert.NotEqual(expected,LaneContinuity.DescribeTurn(g.Curves[1],Vector3.UnitX,Vector3.Zero,true));
        Assert.Equal(LaneContinuityRole.Dead,actual.Role);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitUsesTheLastEndRayAndItsPosition(bool backward)
    {
        var g=Reverse(Merge(backward));var point=Point(g,false);var actual=Side(g,false);
        var terminal=g.Curves.Single(c=>c.Index==0);
        var expected=LaneContinuity.DescribeTurn(terminal,point.StandardTangent,point.Origin,false);
        Assert.Equal(0,actual.TurnSourceCurveIndex);Assert.Equal(expected,actual.Turn);
        Assert.NotEqual(expected,LaneContinuity.DescribeTurn(g.Curves[1],point.StandardTangent,point.Origin,false));
    }

    [Theory]
    [InlineData(true,LaneTurnKind.ForwardRight)]
    [InlineData(false,LaneTurnKind.ForwardLeft)]
    public void CoincidentSplitAndMergeUseTheTwoRaysAtTheSharedEndpoint(bool merge,LaneTurnKind expected)
    {
        var origin=Vector3.Zero;
        var g=new NativeLaneGeometry(42,[
            C(0,new(-20,0,0),origin,[2,3],[]) with{StartTangent=new(10,0,5),EndTangent=new(10,0,-5)},
            C(1,new(-20,0,5),origin,[2,3],[]),
            C(2,origin,new(20,0,0),[],[0,1]),C(3,origin,new(20,0,5),[],[0,1])])
            {InputCurveIndices=[0,1],OutputCurveIndices=[2,3]};
        if(!merge)g=Reverse(g);
        var turn=LaneContinuity.DescribeTurn(g,0,merge?Vector3.UnitX:-Vector3.UnitX,origin,merge);
        Assert.Equal(expected,turn.Turn);
        Assert.Equal(0,turn.SourceCurveIndex);
        Assert.Null(turn.MapIssue);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedPrefabPortStopsEvenWhenThereIsAnotherNativeLink(bool split)
    {
        var g=Merge();g=g with{Curves=g.Curves.Select(c=>c.Index==0?c with{Previous=[9]}:c)
            .Append(C(9,new(-200,0,30),new(-100,0,-3),[0],[]) with{Blinker=LaneBlinker.Left}).ToArray()};
        if(split)g=Reverse(g);
        var side=Side(g,!split);Assert.Equal(0,side.TurnSourceCurveIndex);Assert.Null(side.MapIssue);
        Assert.False(side.VetoedByBlinker);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompletedOriginAtTheSegmentsStartStopsEarlierCurves(bool split)
    {
        var g=Merge();g=g with{InputCurveIndices=[7,8,2],Curves=g.Curves.Select(c=>c.Index==0?c with{Previous=[7,8]}:c)
            .Concat([C(7,new(-200,0,0),new(-100,0,-3),[0],[]),C(8,new(-200,0,20),new(-100,0,-3),[0],[])]).ToArray()};
        if(split)g=Reverse(g);
        Assert.Equal(0,Side(g,!split).TurnSourceCurveIndex);Assert.Null(Side(g,!split).MapIssue);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOriginOnThePreviousCurvesEndExcludesThatPreviousCurve(bool split)
    {
        var g=Merge();g=g with{Curves=g.Curves.Select(c=>c.Index==0?c with{Next=[1,7]}:c)
            .Append(C(7,new(-40,0,3),new(-20,0,20),[],[0])).ToArray(),OutputCurveIndices=[3,7]};
        if(split)g=Reverse(g);
        Assert.Equal(1,Side(g,!split).TurnSourceCurveIndex);Assert.Null(Side(g,!split).MapIssue);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenChainWithoutAnExplicitPortDoesNotInventABoundary(bool split)
    {
        var g=Merge() with{InputCurveIndices=[]};if(split)g=Reverse(g);
        var side=Side(g,!split);Assert.Equal(LaneTurnKind.Unknown,side.Turn);Assert.Null(side.TurnSourceCurveIndex);
        Assert.IsType<MapQueryFailure>(side.MapIssue);
        Assert.Equal(split?LaneContinuityRole.Newborn:LaneContinuityRole.Dead,side.Role);
        Assert.Equal(LaneContinuityRole.Survivor,Assert.Single(Point(g,!split).Branches,b=>b.CurveIndex==2).Role);
    }
    [Fact]
    public void EarlierUnknownStopsWordingButCannotUndoAnImmediateVeto()
    {
        var merge = Merge();
        var g = merge with
        {
            InputCurveIndices = [2],
            Curves = merge.Curves.Select(c => c.Index == 1 ? c with { Blinker = LaneBlinker.Right } : c).ToArray()
        };
        var point=Point(g);var side=Assert.Single(point.Branches, b => b.CurveIndex == 1);
        Assert.Null(point.MapIssue);Assert.Equal(LaneContinuityRole.Dead,side.Role);Assert.True(side.VetoedByBlinker);
        Assert.Null(side.AngleDegrees);Assert.Equal(LaneTurnKind.Unknown,side.Turn);
        var issue = Assert.IsType<MapQueryFailure>(side.MapIssue);
        Assert.Equal(MapQueryFailureKind.InvalidData, issue.Kind);
        Assert.Contains("without a confirmed prefab input boundary", issue.Reason);
        Assert.Equal(LaneContinuityRole.Survivor,Assert.Single(point.Branches,b=>b.CurveIndex==2).Role);
    }
    [Fact]
    public void GraphAwarePublicDescriptionSharesTheSameBoundaryTrace()
    {
        var g=Merge();var described=LaneContinuity.DescribeTurn(g,1,Vector3.UnitX,Vector3.Zero,true);
        Assert.Equal(Side(g).Turn,described.Turn);Assert.Equal(0,described.SourceCurveIndex);Assert.Null(described.MapIssue);
    }
}
