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
    public void CrossingJunctionThroughLaneIsNotReplacedByAnExitAction()
    {
        var s=Scene();
        var route=s.Route.Select(e=>e.Id==s.Route[^1].Id?e with{Junction=new(3,3,1)}:e).ToArray();
        var straight=s.Action with{Kind=ManeuverKind.Straight};
        Assert.Equal(ManeuverKind.Straight,Assert.Single(new LaneExitApproach().Apply([straight],route,s.Graph,0)).Kind);
        Assert.Empty(new LaneExitApproach().Apply([],route,s.Graph,0));
    }

    [Fact]
    public void DedicatedExitStartsAtNativeDivergenceAndRemainsConsumedAfterOpeningLeavesRoute()
    {
        var s=Scene();var detector=new LaneExitApproach();
        Assert.Equal(20,Assert.Single(detector.Apply([s.Action],s.Route,s.Graph,0)).DistanceMeters);
        var trimmed=Assert.Single(detector.Apply([s.Action with{DistanceMeters=200}],s.Route.Skip(1).ToArray(),s.Graph,0));
        Assert.Equal(-80,trimmed.DistanceMeters);
        Assert.Equal(200,trimmed.EndDistanceMeters);
        detector.Reset();
        Assert.Equal(200,Assert.Single(detector.Apply([s.Action with{DistanceMeters=200}],s.Route.Skip(1).ToArray(),s.Graph,0)).DistanceMeters);
    }
    [Fact]
    public void DedicatedLaneNeverCreatesAnActionOrReplacesAStraightDecision()
    {
        var s=Scene();var detector=new LaneExitApproach();
        Assert.Empty(detector.Apply([],s.Route,s.Graph,0));
        var straight=s.Action with{Kind=ManeuverKind.Straight};
        Assert.Equal(straight,Assert.Single(detector.Apply([straight],s.Route,s.Graph,0)));
        Assert.Equal(s.Action with{DistanceMeters=20,EndDistanceMeters=300},Assert.Single(detector.Apply([s.Action],s.Route,s.Graph,0)));
        // Previously cached lead distances must not revive absent or changed actions.
        Assert.Empty(detector.Apply([],s.Route,s.Graph,0));
        Assert.Equal(straight,Assert.Single(detector.Apply([straight],s.Route,s.Graph,0)));
    }

    [Fact]
    public void RecognizedRoundaboutDoesNotGainDedicatedExitLaneGuidance()
    {
        var s=Scene();
        var route=s.Route.Select(e=>e.Id==s.Route[^1].Id?e with{Roundabout=new("ring",1,0)}:e).ToArray();
        Assert.Empty(new LaneExitApproach().Apply([],route,s.Graph,0));
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

    [Fact]
    public void OpeningBelongsToExclusiveLaneSetAndEndsStrictlyAtForkEntry()
    {
        var s=Scene();var action=s.Action with{Kind=ManeuverKind.ChooseLanes,
            ForkInstruction=new(ManeuverKind.ChooseLanes,false,"走最右侧车道",[2],[2])};
        var moved=Assert.Single(new LaneExitApproach().Apply([action],s.Route,s.Graph,0));
        Assert.Equal(20,moved.DistanceMeters);
        Assert.Equal(300,moved.EndDistanceMeters);
        Assert.Single(new LaneExitApproach().Apply([action],s.Route,s.Graph,300));
        Assert.Empty(new LaneExitApproach().Apply([action],s.Route,s.Graph,300.01));
    }

    [Fact]
    public void LeftDedicatedLaneAlsoUsesNativePrefixAndLocalLaneMapping()
    {
        var s=Scene();
        var route=s.Route.Select(e=>e with{LanePaths=e.LanePaths.Select(p=>p with
            {EntryOrder=p.EntryLaneCount-1-p.EntryOrder,ExitOrder=p.ExitLaneCount-1-p.ExitOrder}).ToArray()}).ToArray();
        var graph=s.Graph.Select(e=>e with{LanePaths=e.LanePaths.Select(p=>p with
            {EntryOrder=p.EntryLaneCount-1-p.EntryOrder,ExitOrder=p.ExitLaneCount-1-p.ExitOrder}).ToArray()}).ToArray();
        Assert.Equal(20,Assert.Single(new LaneExitApproach().Apply([s.Action with{Kind=ManeuverKind.KeepLeft}],route,graph,0)).DistanceMeters);
    }

    [Fact]
    public void IndependentlyAddedLaneBecomesAvailableAtSharedRoadCorridor()
    {
        var s=Scene();var own=s.Route[0] with{LanePaths=[Lane(1,10,11,0,0,2,3,[1],s.Route[0].Points.ToArray(),[0]),
            Lane(1,10,11,1,1,2,3,[2],s.Route[0].Points.ToArray(),[0])]};
        DirectedRoad side=new("prefab:1:2:1",99,11,[new(100,0,20),new(100,0,0)])
            {LanePaths=[Lane(1,99,11,0,2,1,3,[3],s.Route[0].Points.ToArray(),[0])]};
        var route=new[]{own,s.Route[1],s.Route[2]};
        var moved=Assert.Single(new LaneExitApproach().Apply([s.Action],route,[own,s.Route[1],s.Route[2],s.Graph[^1],side],0));
        Assert.Equal(100,moved.DistanceMeters);
        Assert.Equal(300,moved.EndDistanceMeters);
        // Without an ordinary-road corridor, independent entrances do not establish
        // permission to move laterally inside the prefab.
        Assert.Equal(100,Assert.Single(new LaneExitApproach().Apply([s.Action with{DistanceMeters=100}],
            [own,s.Route[2]],[own,s.Route[2],s.Graph[^1],side],0)).DistanceMeters);
    }
}
