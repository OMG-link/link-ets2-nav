using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NativeMergeSurvivingOrderTests
{
    private static NativeLaneCurve Curve(int id,Vector3 start,Vector3 end,int[] next,int[] previous)
        =>new(id,start,end,new(end.X-start.X,0,0),new(end.X-start.X,0,0),next,previous);
    private static NativeLaneGeometry Geometry(bool tie,params NativeLaneCurve[] extra)=>NativeLaneFixture.WithInterfaces(42,
        [Curve(0,new(-100,0,tie?-3:0),Vector3.Zero,[2],[]),
         Curve(1,new(-100,0,3),Vector3.Zero,[2],[]),
         Curve(2,Vector3.Zero,new(100,0,0),[],[0,1]),..extra]);
    private static PrefabLanePath Path(NativeLaneGeometry g,int order,int[] curves,int count=3,int exitLane=0,ulong entry=10,ulong exit=20)
    {
        var points=curves.SelectMany(i=>{var c=g.Curves.Single(c=>c.Index==i);return new[]{c.Start,c.End};}).ToArray();
        return new(42,entry,exit,order,exitLane,order,exitLane,count,2,curves,points,new LanePolyline(points).Length)
            {NativeGeometry=g,CurveStartPointIndices=Enumerable.Range(0,curves.Length).Select(i=>i*2).ToArray()};
    }
    private static NativeMergeContinuity Read(NativeLaneGeometry g,params PrefabLanePath[] paths)
        =>NativeMergeContinuity.Read(paths[0],paths[1],1,LaneContinuity.Analyze(g),paths)!;
    private static NativeLaneCurve Untouched()=>Curve(3,new(-100,0,8),new(100,0,8),[],[]);
    [Fact]
    public void ASeparateSurvivingOutputLaneRemainsRecommended()
    {
        var g=Geometry(false,Untouched());
        var result=Read(g,Path(g,0,[0,2]),Path(g,1,[1,2]),Path(g,2,[3],exitLane:1));
        Assert.Equal([0,2],result.SurvivingOrders);Assert.False(result.FirstEnds);Assert.True(result.MoveLeft);
    }
    [Fact]
    public void ADeathAtAnotherMergeIsNotAnUntouchedSurvivor()
    {
        var g=Geometry(false,Curve(4,new(-100,0,14),new(0,0,8),[6],[]),
            Curve(5,new(-100,0,8),new(0,0,8),[6],[]),Curve(6,new(0,0,8),new(100,0,8),[],[4,5]));
        var result=Read(g,Path(g,0,[0,2],4),Path(g,1,[1,2],4),Path(g,2,[4,6],4,1),Path(g,3,[5,6],4,1));
        Assert.Equal([0,3],result.SurvivingOrders);
    }
    [Fact]
    public void APathThatSurvivesThisMergeButDiesLaterIsNotRecommended()
    {
        var g=Geometry(false,Curve(4,new(100,0,0),new(200,0,8),[6],[2]),
            Curve(5,new(100,0,8),new(200,0,8),[6],[]),Curve(6,new(200,0,8),new(300,0,8),[],[4,5]));
        g=g with{Curves=g.Curves.Select(c=>c.Index==2?c with{Next=[4]}:c).ToArray()};
        var a=Path(g,0,[0,2,4,6]);var b=Path(g,1,[1,2,4,6]);var untouched=Path(g,2,[5,6]);
        var result=NativeMergeContinuity.Read(a,b,3,LaneContinuity.Analyze(g),[a,b,untouched])!;
        Assert.Equal([2],result.SurvivingOrders);Assert.False(result.FirstEnds);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewbornSplitBranchIsNotAnUnchangedSurvivor(bool born)
    {
        var g=Geometry(false,Curve(3,new(-100,0,8),new(0,0,8),[4,5],[]),
            Curve(4,new(0,0,8),new(100,0,11),[],[3]),Curve(5,new(0,0,8),new(100,0,8),[],[3]));
        var result=Read(g,Path(g,0,[0,2]),Path(g,1,[1,2]),Path(g,2,born?[3,4]:[3,5],exitLane:1));
        Assert.Equal(born?[0]:new[]{0,2},result.SurvivingOrders);
    }
    [Theory]
    [InlineData(10UL,30UL)]
    [InlineData(99UL,20UL)]
    public void OtherOutletsOrEntrancesAreNotRecommended(ulong entry,ulong exit)
    {
        var g=Geometry(false,Untouched());
        var result=Read(g,Path(g,0,[0,2]),Path(g,1,[1,2]),Path(g,2,[3],exitLane:1,entry:entry,exit:exit));
        Assert.Equal([0],result.SurvivingOrders);
    }
    [Fact]
    public void LocallyAllDeadDoesNotKillAnUnrelatedUnchangedLane()
    {
        var g=Geometry(true,Untouched());var a=Path(g,0,[0,2]);var b=Path(g,1,[1,2]);var c=Path(g,2,[3],exitLane:1);
        var result=Read(g,a,b,c);Assert.True(result.AllDead);Assert.Null(result.FirstEnds);Assert.Null(result.MoveLeft);
        Assert.Equal([2],result.SurvivingOrders);
        var edge=new DirectedRoad("prefab:2A:0:1",10,20,a.Points){LanePaths=[a,b,c]};
        var road=new DirectedRoad("road",5,10,[new(-700,0,0),new(-100,0,0)]);
        var observed=Assert.Single(new LaneMergeDetector().Find([edge],[road,edge],new([new(edge.Id,a,0)],[],true,null),0));
        Assert.False(observed.AllIncomingLanesDie);Assert.Null(observed.MoveLeft);Assert.Equal([2],observed.SurvivingEntryOrders);
    }
    [Fact]
    public void AnUnknownUnrelatedLaneDoesNotBecomeAConfirmedDeath()
    {
        var g=Geometry(true,Curve(4,new(-100,0,14),new(0,0,8),[6],[]),
            Curve(5,new(-100,0,8),new(0,0,8),[6],[]),Curve(6,new(0,0,8),new(100,0,8),[],[4,5]) with{StartTangent=Vector3.Zero});
        var a=Path(g,0,[0,2]);var b=Path(g,1,[1,2]);var unknown=Path(g,2,[4,6],exitLane:1);
        var result=Read(g,a,b,unknown);Assert.True(result.AllDead);Assert.Null(result.FirstEnds);Assert.Empty(result.SurvivingOrders);
        var edge=new DirectedRoad("prefab:2A:0:1",10,20,a.Points){LanePaths=[a,b,unknown]};
        var road=new DirectedRoad("road",5,10,[new(-700,0,0),new(-100,0,0)]);
        var observed=Assert.Single(new LaneMergeDetector().Find([edge],[road,edge],new([new(edge.Id,a,0)],[],true,null),0));
        Assert.False(observed.AllIncomingLanesDie);Assert.Empty(observed.SurvivingEntryOrders);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternativeNativeChainsDoNotMakeRecommendationDependOnEnumerationOrder(bool reverse)
    {
        var g=Geometry(false,Curve(3,new(-100,0,8),new(0,0,8),[4,5],[]),
            Curve(4,new(0,0,8),new(100,0,8),[6],[3]) with{StartTangent=new(100,0,10),EndTangent=new(100,0,10)},
            Curve(5,new(0,0,8),new(100,0,8),[6],[3]),Curve(6,new(100,0,8),new(200,0,8),[],[4,5]));
        var a=Path(g,0,[0,2]);var b=Path(g,1,[1,2]);var dead=Path(g,2,[3,4,6],exitLane:1);var alive=Path(g,2,[3,5,6],exitLane:1);
        var paths=new[]{a,b}.Concat(reverse?new[]{alive,dead}:new[]{dead,alive}).ToArray();
        var edge=new DirectedRoad("prefab:2A:0:1",10,20,a.Points){LanePaths=paths};
        var road=new DirectedRoad("road",5,10,[new(-700,0,0),new(-100,0,0)]);
        var observed=Assert.Single(new LaneMergeDetector().Find([edge],[road,edge],new([new(edge.Id,a,0)],[],true,null),0));
        Assert.Equal([0,2],observed.SurvivingEntryOrders);
    }
    [Fact]
    public void UnresolvedTurnDirectionDoesNotUndoConfirmedLocalDeaths()
    {
        var g=Geometry(false);g=g with{Curves=g.Curves.Select(c=>c.Index==1?c with{Start=g.Curves[0].Start}:c).ToArray()};
        var a=Path(g,0,[0,2],2);var b=Path(g,1,[1,2],2);
        var result=Read(g,a,b);Assert.True(result.AllDead);Assert.Null(result.FirstEnds);Assert.Null(result.MoveLeft);
        Assert.Empty(result.SurvivingOrders);Assert.True(result.AllIncomingLanesDie);
    }
}
