using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class LaneExitApproachTests
{
    private static PrefabLanePath Lane(ulong uid,ulong from,ulong to,int input,int output,int inputs,int outputs,
        int[] curves,Vector3[] points,int[] starts)=>new(uid,from,to,input,output,input,output,inputs,outputs,curves,points,100){CurveStartPointIndices=starts};
    private static (DirectedRoad[] Route,DirectedRoad[] Graph,NavigationManeuver Action) Scene()
    {
        Vector3[] points=[new(0,0,0),new(20,0,0),new(100,0,0)];
        var added=Lane(1,10,11,1,2,2,3,[1,2],points,[0,1]);
        var kept=Lane(1,10,11,1,1,2,3,[1,3],points,[0,1]);
        DirectedRoad addition=new("prefab:1:0:1",10,11,points){LanePaths=[added,kept]};
        DirectedRoad road=new("road",11,20,[new(100,0,0),new(300,0,0)]);
        var exit=Lane(2,20,21,2,0,3,1,[5,6],[new(300,0,0),new(350,0,20)],[0,1]);
        DirectedRoad fork=new("prefab:2:0:1",20,21,[new(300,0,0),new(350,0,20)]){LanePaths=[exit]};
        DirectedRoad main=new("prefab:2:0:2",20,22,[new(300,0,0),new(400,0,0)])
            {LanePaths=[Lane(2,20,22,0,0,3,2,[7],points,[0]),Lane(2,20,22,1,1,3,2,[8],points,[0])]};
        DirectedRoad[] route=[addition,road,fork];
        return(route,[..route,main],new("exit",ManeuverKind.KeepRight,300,"in","out"){JunctionId="prefab:2"});
    }
    [Fact]
    public void DedicatedExitStartsAtNativeDivergenceAndRemainsConsumedAfterOpeningLeavesRoute()
    {
        var s=Scene();var detector=new LaneExitApproach();
        Assert.Equal(20,Assert.Single(detector.Apply([s.Action],s.Route,s.Graph,0)).DistanceMeters);
        Assert.Equal(0,Assert.Single(detector.Apply([s.Action with{DistanceMeters=200}],s.Route.Skip(1).ToArray(),s.Graph,0)).DistanceMeters);
        detector.Reset();
        Assert.Equal(200,Assert.Single(detector.Apply([s.Action with{DistanceMeters=200}],s.Route.Skip(1).ToArray(),s.Graph,0)).DistanceMeters);
    }
    [Fact]
    public void SharedMainLaneAndUnknownLaneTopologyDoNotMoveExit()
    {
        var s=Scene();var detector=new LaneExitApproach();
        var main=s.Graph[^1] with{LanePaths=[s.Route[^1].LanePaths[0] with{ExitNodeUid=22}]};
        Assert.Equal(300,Assert.Single(detector.Apply([s.Action],s.Route,[..s.Route,main],0)).DistanceMeters);
        Assert.Equal(300,Assert.Single(new LaneExitApproach().Apply([s.Action],s.Route.Select(e=>e with{LanePaths=[]}).ToArray(),s.Graph,0)).DistanceMeters);
    }
    [Fact]
    public void MissingNativeDivergenceAndOtherJunctionBeforeExitDoNotMoveCue()
    {
        var s=Scene();var invalid=s.Route[0] with{LanePaths=s.Route[0].LanePaths.Select(p=>p with{CurveStartPointIndices=[]}).ToArray()};
        Assert.Equal(300,Assert.Single(new LaneExitApproach().Apply([s.Action],[invalid,..s.Route.Skip(1)],s.Graph,0)).DistanceMeters);
        DirectedRoad side=new("prefab:1:0:2",10,99,[Vector3.Zero,Vector3.UnitZ]);
        Assert.Equal(300,Assert.Single(new LaneExitApproach().Apply([s.Action],s.Route,[..s.Graph,side],0)).DistanceMeters);
    }
}
