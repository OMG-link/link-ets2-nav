using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class NativeMergeGuidanceTests
{
    private static (PrefabLanePath A,PrefabLanePath B,LaneContinuityResult Result) Graph(bool tie)
    {
        var a=new NativeLaneCurve(0,new(-100,0,tie?-3:0),Vector3.Zero,new(100,0,0),new(100,0,0),[2],[]);
        var b=new NativeLaneCurve(1,new(-100,0,3),Vector3.Zero,new(100,0,0),new(100,0,0),[2],[]);
        var tail=new NativeLaneCurve(2,Vector3.Zero,new(100,0,0),new(100,0,0),new(100,0,0),[],[0,1]);
        var native=NativeLaneFixture.WithInterfaces(42,[a,b,tail]);
        PrefabLanePath Path(NativeLaneCurve c,int order)=>new(42,10,20,order,0,order,0,2,1,[c.Index,2],
            [c.Start,Vector3.Zero,tail.End],200){NativeGeometry=native,CurveBlinkers=[order==0?LaneBlinker.Right:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        return(Path(a,0),Path(b,1),LaneContinuity.Analyze(native));
    }
    [Fact]
    public void PrimitiveSurvivorAndDirectionTakePrecedenceOverContradictoryLamps()
    {
        var (a,b,analysis)=Graph(false);
        var role=NativeMergeContinuity.Read(a,b,1,analysis,[a,b])!;
        Assert.False(role.FirstEnds);Assert.True(role.MoveLeft);Assert.False(role.AllDead);
        Assert.Equal([0],role.SurvivingOrders);
    }
    [Fact]
    public void NoUniqueMainMeansAllDieWithoutAnInventedKeptLane()
    {
        var (a,b,analysis)=Graph(true);
        var role=NativeMergeContinuity.Read(a,b,1,analysis,[a,b])!;
        Assert.True(role.AllDead);Assert.Null(role.FirstEnds);Assert.Null(role.MoveLeft);Assert.Empty(role.SurvivingOrders);
    }
    [Fact]
    public void GenericReductionIsIndependentOfComponentNavigationAndDoesNotClaimASurvivor()
    {
        var navigation=new NavigationManeuver("component:42:exit",ManeuverKind.KeepRight,20,"a","b")
        {JunctionId="prefab:2A",EndDistanceMeters=100};
        var reduction=new LaneMergeObservation("physical:42",42,null,null,null,null,Vector3.Zero,null,0,600,LaneMergeKind.LaneReduction,80,"all die")
        {EndDistanceMeters=80,AllIncomingLanesDie=true,ApproachLaneCount=2};
        var actions=LaneContractionGuidance.Apply([navigation],[reduction]);
        Assert.Contains(navigation,actions);
        var environment=Assert.Single(actions.Where(a=>a.AnnouncementMode==NavigationAnnouncementMode.DistancePreview));
        Assert.Equal("车道合并，请注意观察",environment.Instruction);Assert.Equal(80,environment.EndDistanceMeters);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortNativeMergeDoesNotNeedAnOldParallelOrMinimumLengthHeuristic(bool allDie)
    {
        var a=new NativeLaneCurve(0,new(0,0,allDie?-3:0),new(2,0,0),new(2,0,allDie?3:0),new(2,0,allDie?3:0),[2],[]);
        var b=new NativeLaneCurve(1,new(0,0,3),new(2,0,0),new(2,0,-3),new(2,0,-3),[2],[]);
        var tail=new NativeLaneCurve(2,new(2,0,0),new(2.5f,0,0),new(.5f,0,0),new(.5f,0,0),[],[0,1]);
        var native=NativeLaneFixture.WithInterfaces(42,[a,b,tail]);
        PrefabLanePath Path(NativeLaneCurve curve,int order)=>new(42,10,20,order,0,order,0,2,1,[curve.Index,2],
            [curve.Start,curve.End,tail.End],4){NativeGeometry=native,CurveStartPointIndices=[0,1]};
        var first=Path(a,0);var second=Path(b,1);
        var edge=new DirectedRoad("prefab:000000000000002A:000000000000000A:0000000000000014",10,20,first.Points){LanePaths=[first,second]};
        var road=new DirectedRoad("road:up",5,10,[new(-600,0,0),Vector3.Zero]);
        var plan=new LaneNavigationPlan([new(edge.Id,second,0)],[],true,null);
        var observations=new LaneMergeDetector().Find([edge],[road,edge],plan,0);
        Assert.Contains(observations,m=>m.Kind==LaneMergeKind.LaneReduction);
        Assert.All(observations,m=>Assert.Null(m.ParallelDistanceMeters));
        if(allDie)Assert.All(observations,m=>{Assert.True(m.AllIncomingLanesDie);Assert.Empty(m.SurvivingEntryOrders);});
    }
}
