using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;
// Synthetic finite graph tests verify traversal semantics, not real game reachability.
public sealed class LaneTraceLoopTests
{
    private static PrefabLanePath Lane(ulong prefab,ulong entry,ulong exit,int order,double length=20)
        =>new(prefab,entry,exit,order,order,order,order,2,2,[order],[Vector3.Zero,new(0,0,(float)length)],length);
    private static DirectedRoad Edge(string id,params PrefabLanePath[] paths)
        =>new(id,paths[0].EntryNodeUid,paths[0].ExitNodeUid,paths[0].Points){LanePaths=paths};
    private static DirectedRoad Road(string id,ulong from,ulong to,double length)
        =>new(id,from,to,[Vector3.Zero,new(0,0,(float)length)]);
    [Fact]
    public void RepeatedNativePairIsActuallyTracedUntilTheSingleBudget()
    {
        var ending=Lane(42,10,20,1);var main=Lane(42,10,20,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:10:20",ending,main),Road("start",30,10,10),
            Edge("prefab:43:100:30",Lane(43,100,30,1),Lane(43,100,30,0)),Road("a",110,100,10),
            Edge("prefab:44:200:110",Lane(44,200,110,1),Lane(44,200,110,0)),Road("b",30,200,10)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,10,main,10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);Assert.Equal(500,result.BackwardMeters);
        Assert.True(result.Steps.Count>=12);Assert.True(result.Steps.Count(s=>s.PrefabUid==43)>=6);
        Assert.All(result.Steps.Zip(result.Steps.Skip(1)),pair=>Assert.True(pair.First.BackwardMeters<pair.Second.BackwardMeters));
    }
    [Fact]
    public void APositiveOrdinaryRoadCycleReachesTheMeasuredBudget()
    {
        var ending=Lane(42,10,20,1);var main=Lane(42,10,20,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:10:20",ending,main),
            Road("start",30,10,70),Road("a",40,30,70),Road("b",30,40,70)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,10,main,10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);Assert.Equal(500,result.BackwardMeters);
    }
    [Fact]
    public void AZeroRoadCannotMasqueradeAsACompletedSourceCycle()
    {
        var ending=Lane(42,10,20,1);var main=Lane(42,10,20,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:10:20",ending,main),Road("zero",30,10,0)]);
        Assert.Throws<ArgumentException>(()=>new LaneMergeOriginClassifier(graph).Classify(ending,10,main,10));
    }
    [Fact]
    public void AZeroNativeLaneCannotBecomeAContinuingComponent()
    {
        var main=Edge("prefab:42:10:20",Lane(42,10,20,0,0)) with{JoinPriority=new(10,[],[],8,"main")};
        var side=Edge("prefab:42:11:20",Lane(42,11,20,1,0)) with{JoinPriority=main.JoinPriority};
        var graph=SourceGeometryFixture.Graph([main,side]);
        Assert.Throws<ArgumentException>(()=>ComponentContinuityIndex.Build(graph));
    }
    [Theory][InlineData(-1)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidQueryPrefixIsAnExplicitError(double prefix)
    {
        var a=Lane(42,10,20,1);var b=Lane(42,10,20,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:10:20",a,b)]);
        Assert.Throws<ArgumentException>(()=>new LaneMergeOriginClassifier(graph).Classify(a,prefix,b,0));
    }
    [Fact]
    public void UnrepresentablePositiveProgressIsRejectedRatherThanSpinning()
        =>Assert.Throws<ArgumentException>(()=>LaneTraceDistance.Advance(500,double.Epsilon));
    [Fact]
    public void ReturningSideLaneDoesNotHideTheLossOfItsMainIdentityByClaimingFiveHundredMetres()
    {
        NativeLaneCurve C(int id,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(id,a,b,b-a,b-a,next,previous);
        var geo=new NativeLaneGeometry(1,[C(0,Vector3.Zero,new(90,0,0),[2],[]),
            C(1,new(0,0,-30),new(90,0,0),[2],[]),C(2,new(90,0,0),new(100,0,0),[],[0,1]),
            C(3,new(0,0,-4),new(100,0,-4),[],[])]){InputCurveIndices=[0,1,3],OutputCurveIndices=[2,3]};
        PrefabLanePath P(ulong entry,int input,int output,int[] curves)
        {
            var points=curves.Select(i=>geo.Curves[i].Start).Append(geo.Curves[curves[^1]].End).ToArray();
            return new(1,entry,30,input,output,input,output,entry==10?1:2,2,curves,points,new LanePolyline(points).Length)
                {NativeGeometry=geo,CurveStartPointIndices=Enumerable.Range(0,curves.Length).ToArray()};
        }
        var main=Edge("prefab:1:10:30",P(10,0,0,[0,2])) with{JoinPriority=new(10,[],[],8,"main")};
        var side=Edge("prefab:1:20:30",P(20,0,0,[1,2]),P(20,1,1,[3])) with{JoinPriority=main.JoinPriority};
        var next=NativeLaneFixture.WithInterfaces(2,[C(0,new(110,0,0),new(120,0,0),[],[]),C(1,new(110,0,-4),new(120,0,-4),[],[])]);
        var nextPaths=Enumerable.Range(0,2).Select(order=>new PrefabLanePath(2,40,50,order,order,order,order,2,2,[order],
            [next.Curves[order].Start,next.Curves[order].End],10){NativeGeometry=next,CurveStartPointIndices=[0]}).ToArray();
        var index=ComponentContinuityIndex.Build(new([main,side,Road("out",30,40,10),Edge("prefab:2:40:50",nextPaths),Road("return",50,20,120)]));
        var result=index.ForEntry(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Unknown,result.Kind);
        Assert.Equal(ComponentLaneFate.Unresolved,result.Lanes.Single(l=>l.EntryOrder==1).Fate);
        Assert.Equal(MapQueryFailureKind.Ambiguous,Assert.IsType<MapQueryFailure>(result.Issue).Kind);
    }
}
