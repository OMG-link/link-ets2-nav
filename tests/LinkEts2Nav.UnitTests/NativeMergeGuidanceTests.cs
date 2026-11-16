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
        var native=new NativeLaneGeometry(42,[a,b,tail]);
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
}
