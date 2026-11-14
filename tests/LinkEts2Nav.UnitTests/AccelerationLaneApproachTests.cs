using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class AccelerationLaneApproachTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void NativeMergeLampsAreKeptAndAConflictNeverProducesTheScoredOppositeCommand(bool conflict)
    {
        var scene=Scene(true);
        var graph=scene.Graph.Select(e=>e.Id=="prefab:2:0:1"?e with{LanePaths=e.LanePaths.Select(p=>p with
            {CurveBlinkers=p.EntryLane==2?[conflict?LaneBlinker.NoBlinker:LaneBlinker.Left,LaneBlinker.NoBlinker]
                :p.EntryLane==1?[conflict?LaneBlinker.Left:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]:p.CurveBlinkers}).ToArray()}:e).ToArray();
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(graph),TestContext.Current.CancellationToken);
        var terminal=Assert.Single(index.Corridors).Terminals.Single(t=>t.Merges);
        Assert.Equal(conflict,terminal.DirectionConflict);
        var route=scene.Route.Select(e=>graph.Single(g=>g.Id==e.Id)).ToArray();
        var actions=AuxiliaryLaneGuidance.Apply([],route,new([],[],true,null),index);
        if(conflict)
        {
            Assert.Empty(actions);Assert.Contains(index.Diagnostics,d=>d.Contains("原生合流转向灯"));
        }
        else
        {
            Assert.Equal(true,terminal.NativeMoveLeft);Assert.Equal(ManeuverKind.MergeLeft,Assert.Single(actions).Kind);
        }
    }

    [Fact]
    public void ProvenShortNativeCorridorSupersedesLongGeometricHistoryWithoutDuplicatingEnvironmentSpeech()
    {
        var shape=Scene(true);var map=new TruckLib.ScsMap.Map();
        var mainRoad=Road.Add(map,new(0,0,-1000),new(0,0,60),default);
        var sideRoad=Road.Add(map,new(-4.5f,0,-1000),new(-4.5f,0,60),default);
        var body=Road.Add(map,new(0,0,100),new(0,0,300),default);
        var outgoing=Road.Add(map,new(0,0,340),new(0,0,1000),default);
        var sideExit=Road.Add(map,new(-20,0,340),new(-100,0,440),default);
        var nodes=new Dictionary<ulong,ulong>{{1,mainRoad.ForwardNode.Uid},{2,sideRoad.ForwardNode.Uid},
            {3,body.Node.Uid},{4,body.ForwardNode.Uid},{5,outgoing.Node.Uid},{6,sideExit.Node.Uid}};
        var prefabs=shape.Graph.Where(e=>e.Id.StartsWith("prefab:")).Select(e=>e with{StartNodeUid=nodes[e.StartNodeUid],EndNodeUid=nodes[e.EndNodeUid],
            LanePaths=e.LanePaths.Select(p=>p with{EntryNodeUid=nodes[p.EntryNodeUid],ExitNodeUid=nodes[p.ExitNodeUid]}).ToArray(),
            LaneConnections=e.LaneConnections?.Select(c=>c with{EntryNode=nodes[c.EntryNode],ExitNode=nodes[c.ExitNode]}).ToArray()}).ToArray();
        var graph=new RoadPathSource(map,prefabs).GetNavigationGraph().Select(e=>e.Id.StartsWith("prefab:1:")
            ?e with{JoinPriority=new(mainRoad.ForwardNode.Uid,[],[],20,"fixture main entrance")}:e).ToArray();
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(graph),TestContext.Current.CancellationToken);
        var selected=new[]{graph.Single(e=>e.Id=="prefab:1:2:1"),graph.Single(e=>e.Id==body.Uid.ToString("X16")&&e.StartNodeUid==body.Node.Uid),graph.Single(e=>e.Id=="prefab:2:0:1")};
        var ending=selected[^1].LanePaths.Single(p=>p.EntryLane==2);
        var plan=new LaneNavigationPlan([new(selected[^1].Id,ending)],[],true,null);
        var weak=Assert.Single(new LaneMergeDetector().Find(selected,graph,plan,0));
        Assert.Equal(LaneMergeKind.LaneReduction,weak.Kind);Assert.True(weak.ParallelDistanceMeters>500);
        Assert.NotNull(index.NativeMerge(weak));
        var now=DateTimeOffset.UtcNow;
        var route=RouteSnapshot.Parse([new(sideRoad.Node.Uid,2000,100),new(sideRoad.ForwardNode.Uid,1600,80),new(body.Node.Uid,1500,70),
            new(body.ForwardNode.Uid,1300,60),new(outgoing.Node.Uid,1100,50),new(outgoing.ForwardNode.Uid,0,0)],now);
        var data=new GameDataSnapshot(new(-4.5,0,105,.5,15,1500,true,false,1,now),route,RouteAvailability.Available,"s",true);
        var result=new NavigationRuntime().Process(data,new(map,[],[],[]){PrefabPaths=prefabs,AuxiliaryLanes=index},now);
        Assert.Contains(result.LaneMerges,m=>m.Kind==LaneMergeKind.Ramp);
        Assert.Single(result.Upcoming,a=>a.Kind==ManeuverKind.MergeLeft);
        Assert.DoesNotContain(result.Upcoming,a=>a.AnnouncementMode==NavigationAnnouncementMode.DistancePreview);
    }

    private static PrefabLanePath Lane(ulong prefab,ulong from,ulong to,int entry,int output,int inputs,int outputs,
        int[] curves,Vector3[] points,LaneBlinker[]? lamps=null)
        =>new(prefab,from,to,entry,output,entry,output,inputs,outputs,curves,points,40)
            {CurveStartPointIndices=Enumerable.Range(0,curves.Length).ToArray(),CurveBlinkers=lamps??curves.Select(_=>LaneBlinker.NoBlinker).ToArray()};
    private static (DirectedRoad[] Route,DirectedRoad[] Graph) Scene(bool physical,bool leftSide=false,bool permanent=false)
    {
        LaneConnection[] connections=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,2)];
        DirectedRoad side=new("prefab:1:2:1",2,3,[new(-4.5f,0,60),new(-4.5f,0,100)])
            {LaneConnections=connections,JoinPriority=new(1,[],[],20,"test complete score"),
                LanePaths=[Lane(1,2,3,0,2,1,3,[1],[new(-4.5f,0,60),new(-4.5f,0,100)])]};
        DirectedRoad main=new("prefab:1:0:1",1,3,[new(0,0,60),new(0,0,100)])
            {LaneConnections=connections,LanePaths=[Lane(1,1,3,0,0,2,3,[2],[new(4.5f,0,60),new(4.5f,0,100)]),
                Lane(1,1,3,1,1,2,3,[3],[new(0,0,60),new(0,0,100)])]};
        DirectedRoad road=new("road",3,4,[new(0,0,100),new(0,0,300)]);
        var kept=new[]{Lane(2,4,5,0,0,3,2,[12],[new(4.5f,0,300),new(4.5f,0,340)]),
            Lane(2,4,5,1,1,3,2,[11,20],[new(0,0,300),new(0,0,320),new(0,0,340)])};
        var ending=Lane(2,4,5,2,1,3,2,[10,20],[new(-4.5f,0,300),new(0,0,320),new(0,0,340)],[LaneBlinker.Left,LaneBlinker.NoBlinker]);
        DirectedRoad finish=new("prefab:2:0:1",4,5,[new(0,0,300),new(0,0,340)])
            {LanePaths=physical?[..kept,ending]:permanent?[..kept,ending with{ExitLane=2,ExitOrder=2,ExitLaneCount=3,CurveIndices=[21]}]:kept};
        DirectedRoad split=new("prefab:2:0:2",4,6,[new(0,0,300),new(-20,0,340)])
            {LanePaths=[ending with{ExitNodeUid=6,ExitLane=0,ExitOrder=0,ExitLaneCount=1}]};
        DirectedRoad Mirror(DirectedRoad e)=>!leftSide?e:e with{Points=e.Points.Select(p=>p with{X=-p.X}).ToArray(),
            LanePaths=e.LanePaths.Select(p=>p with{EntryOrder=p.EntryLaneCount-1-p.EntryOrder,ExitOrder=p.ExitLaneCount-1-p.ExitOrder,
                Points=p.Points.Select(v=>v with{X=-v.X}).ToArray(),CurveBlinkers=p.CurveBlinkers.Select(l=>l==LaneBlinker.Left?LaneBlinker.Right:l==LaneBlinker.Right?LaneBlinker.Left:l).ToArray()}).ToArray()};
        return(new[]{side,road,finish}.Select(Mirror).ToArray(),new[]{side,main,road,finish,split}.Select(Mirror).ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredNavigationLaneChangeUsesTenPercentOfWholeAddedLaneInterval(bool leftSide)
    {
        var s=Scene(false,leftSide);var action=Assert.Single(new AccelerationLaneApproach().Find(s.Route,s.Graph,0));
        Assert.Equal(leftSide?ManeuverKind.MergeRight:ManeuverKind.MergeLeft,action.Kind);
        Assert.Equal(NavigationJunctionKind.Ramp,action.JunctionKind);
        Assert.StartsWith("prefab:",action.JunctionId);
        Assert.Equal(60,action.DistanceMeters);Assert.Equal(240,action.EndDistanceMeters);
        Assert.Equal(new LaneActionInterval(40,240),action.ApproachInterval);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealSharedSuffixUsesNativeDirectionAndActualMergePoint(bool leftSide)
    {
        var s=Scene(true,leftSide);var action=Assert.Single(new AccelerationLaneApproach().Find(s.Route,s.Graph,0));
        Assert.Equal(leftSide?ManeuverKind.MergeRight:ManeuverKind.MergeLeft,action.Kind);
        Assert.Equal(NavigationJunctionKind.Ramp,action.JunctionKind);
        Assert.StartsWith("prefab:",action.JunctionId);
        Assert.Equal(62,action.DistanceMeters);Assert.Equal(260,action.EndDistanceMeters);
    }
    [Fact]
    public void MiddleInitializationUsesPhysicalIndexAndExplicitLaneSelectionInsteadOfPriorRouteHistory()
    {
        var s=Scene(false);var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(s.Graph),TestContext.Current.CancellationToken);
        var lanes=new LaneNavigationPlan([],[],true,null){InitialTransfer=new("road",s.Route[^1].Id,["road"],2,1,false){AllowedLaneChange=true}};
        var trimmed=Assert.Single(AuxiliaryLaneGuidance.Apply([],s.Route.Skip(1).ToArray(),lanes,index));
        Assert.Equal(20,trimmed.DistanceMeters);Assert.Equal(200,trimmed.EndDistanceMeters);
        var fresh=AuxiliaryLaneIndex.Build(new NativeLaneGraph(s.Graph),TestContext.Current.CancellationToken);
        Assert.Equal(trimmed,Assert.Single(AuxiliaryLaneGuidance.Apply([],s.Route.Skip(1).ToArray(),lanes,fresh)));
    }
    [Fact]
    public void PermanentLaneAdditionAndDriverOnOriginalMainRoadNeedNoMergeInstruction()
    {
        var s=Scene(false,permanent:true);
        Assert.Empty(new AccelerationLaneApproach().Find(s.Route,s.Graph,0));
        var main=s.Graph.Single(e=>e.StartNodeUid==1);
        Assert.Empty(new AccelerationLaneApproach().Find([main,..s.Route.Skip(1)],s.Graph,0));
    }
    [Fact]
    public void MissingMainIdentityAndCrossingJunctionDoNotBecomeRampLaneChanges()
    {
        var s=Scene(false);
        Assert.Empty(new AccelerationLaneApproach().Find([s.Route[0] with{JoinPriority=null},..s.Route.Skip(1)],[s.Graph[0] with{JoinPriority=null},..s.Graph.Skip(1)],0));
        Assert.Empty(new AccelerationLaneApproach().Find([s.Route[0] with{Junction=new(3,3,1)},..s.Route.Skip(1)],[s.Graph[0] with{Junction=new(3,3,1)},..s.Graph.Skip(1)],0));
    }

    [Theory]
    [InlineData(500,true)]
    [InlineData(500.25,false)]
    public void CompleteParallelIntervalIncludesExactlyFiveHundredMeters(float distance,bool ramp)
    {
        var s=Scene(false);float shift=distance-200;
        DirectedRoad Stretch(DirectedRoad e)=>e.Id=="road"?e with{Points=[e.Points[0],e.Points[^1]+new Vector3(0,0,shift)]}
            :e.Id.StartsWith("prefab:2:")?e with{Points=e.Points.Select(p=>p+new Vector3(0,0,shift)).ToArray(),
                LanePaths=e.LanePaths.Select(p=>p with{Points=p.Points.Select(v=>v+new Vector3(0,0,shift)).ToArray()}).ToArray()}:e;
        var actions=new AccelerationLaneApproach().Find(s.Route.Select(Stretch).ToArray(),s.Graph.Select(Stretch).ToArray(),0);
        Assert.Equal(ramp,actions.Count>0);
        if(ramp){var action=Assert.Single(actions);Assert.Equal(500,action.ApproachInterval!.LengthMeters);Assert.Equal(action.EndDistanceMeters!.Value-200,action.DistanceMeters);}
    }
    [Fact]
    public void AdjacentPrefabUsingSamePhysicalUidCannotSupplyAnotherPrefabsLaneExit()
    {
        var s=Scene(false);var unrelated=s.Graph[^1] with{Id="prefab:99:0:1",
            LanePaths=s.Graph[^1].LanePaths.Select(p=>p with{PrefabUid=99}).ToArray()};
        Assert.Empty(new AccelerationLaneApproach().Find(s.Route,[..s.Graph.Take(s.Graph.Length-1),unrelated],0));
    }
}
