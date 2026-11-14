using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class AuxiliaryLaneDirectionTests
{
    [Fact]
    public void AnAuxiliaryLaneMayExitOrContinueNativelyBeforeItsLaterPhysicalMerge()
    {
        var scene=Corridor([0,1],[2],[2]);var exit=scene.Graph.Single(e=>e.Id=="prefab:2:0:2");
        var sideExit=scene.Route[^1];
        var continuing=exit with{LanePaths=[..exit.LanePaths.Select(p=>p with{ExitLaneCount=3}),
            sideExit.LanePaths[0] with{ExitNodeUid=22,ExitLane=2,ExitOrder=2,ExitLaneCount=3,CurveIndices=[29],Points=exit.Points}]};
        var road=new DirectedRoad("later-road",22,30,[exit.Points[^1],exit.Points[^1]+new Vector3(50,0,0)]);
        var start=road.Points[^1];Vector3[] middle=[start,start+new Vector3(10,0,0),start+new Vector3(20,0,0)];
        PrefabLanePath Lane(int input,int output,int[] curves,Vector3[] points)=>new(3,30,31,input,output,input,output,3,2,curves,points,20)
            {CurveStartPointIndices=[0,1],CurveBlinkers=[input==2?LaneBlinker.Left:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        var finish=new DirectedRoad("prefab:3:0:1",30,31,middle){LanePaths=[Lane(0,0,[30,39],middle),Lane(1,1,[31,40],middle),
            Lane(2,1,[32,40],[start+new Vector3(0,0,4.5f),middle[1],middle[2]])]};
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph([..scene.Graph.Where(e=>e.Id!=exit.Id),continuing,road,finish]),TestContext.Current.CancellationToken);
        Assert.Equal(2,index.Corridors.Count);
        var early=index.Corridors.Single(c=>c.EndKind==AuxiliaryBoundaryKind.SeparateExit);
        Assert.False(early.Terminals.Single(t=>t.Edge.Id==continuing.Id).Merges);
        var mainInstruction=RoadForkRules.Decide(continuing.ForkInfo!,22);
        var action=scene.Action with{Kind=mainInstruction.Kind,ForkInstruction=mainInstruction,Instruction=mainInstruction.Text};
        var fromSide=scene.Graph.Single(e=>e.StartNodeUid==99);
        var route=new[]{fromSide,scene.Route[1],continuing,road,finish};
        var guidance=AuxiliaryLaneGuidance.Apply([action],route,new([],[],true,null),index);
        Assert.Equal(NavigationAnnouncementMode.Silent,guidance.Single(a=>a.Id==action.Id).AnnouncementMode);
        var merge=Assert.Single(guidance,a=>a.Kind==ManeuverKind.MergeLeft);
        Assert.Equal("prefab:3",merge.JunctionId);Assert.Equal(route.Take(4).Sum(RouteGeometry.Length)+10,merge.EndDistanceMeters);
        var ownExit=Assert.Single(AuxiliaryLaneGuidance.Apply([scene.Action],[fromSide,scene.Route[1],sideExit],new([],[],true,null),index));
        Assert.Equal(ManeuverKind.KeepRight,ownExit.Kind);
    }

    [Fact]
    public void CorridorPublicationIsReadOnlyAndMapBuildCanBeCancelled()
    {
        var scene=Corridor([0,1],[2],[2]);
        var graph=new NativeLaneGraph(scene.Graph);
        var index=AuxiliaryLaneIndex.Build(graph,TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(()=>((IList<AuxiliaryLaneCorridor>)index.Corridors).Clear());
        Assert.Throws<NotSupportedException>(()=>((IList<AuxiliaryLaneSection>)index.Corridors[0].Sections).Clear());
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(()=>AuxiliaryLaneIndex.Build(graph,cancelled.Token));
        var longLane=Corridor([0,1],[2],[2],501);
        var longIndex=AuxiliaryLaneIndex.Build(new NativeLaneGraph(longLane.Graph),TestContext.Current.CancellationToken);
        Assert.Empty(longIndex.Corridors);Assert.Empty(longIndex.Diagnostics);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void MapCorridorIsSharedByMainEntranceSideEntranceAndMiddleInitialization(bool left)
    {
        var scene=left?Corridor([1,2],[0],[0],363.12482f):Corridor([0,1],[2],[2],363.12482f);
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(scene.Graph),TestContext.Current.CancellationToken);
        var corridor=Assert.Single(index.Corridors);var immutable=index.Corridors;
        Assert.Equal(AuxiliaryBoundaryKind.IndependentEntrance,corridor.StartKind);
        Assert.Equal(AuxiliaryBoundaryKind.SeparateExit,corridor.EndKind);
        Assert.Equal(363.12482,corridor.LengthMeters,4);Assert.Equal(left,corridor.SideIsLeft);
        var sideBirth=scene.Graph.Single(e=>e.StartNodeUid==99);
        var mainExit=scene.Graph.Single(e=>e.Id=="prefab:2:0:2");
        var empty=new LaneNavigationPlan([],[],true,null);
        var fromMain=AuxiliaryLaneGuidance.Apply([scene.Action],scene.Route,empty,index);
        var fromSide=AuxiliaryLaneGuidance.Apply([scene.Action],[sideBirth,..scene.Route.Skip(1)],empty,index);
        Assert.Equal(left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight,Assert.Single(fromMain).Kind);
        Assert.Equal(Assert.Single(fromMain).Kind,Assert.Single(fromSide).Kind);
        Assert.DoesNotContain(fromSide,a=>a.Kind is ManeuverKind.MergeLeft or ManeuverKind.MergeRight);
        var inherited=RoadForkRules.Decide(mainExit.ForkInfo!,mainExit.EndNodeUid);
        var mainAction=scene.Action with{Kind=inherited.Kind,Instruction=inherited.Text,ForkInstruction=inherited};
        var mainRoute=new[]{scene.Route[0],scene.Route[1],mainExit};
        var main=Assert.Single(AuxiliaryLaneGuidance.Apply([mainAction],mainRoute,empty,index));
        Assert.Equal(NavigationAnnouncementMode.Silent,main.AnnouncementMode);
        var required=Assert.Single(AuxiliaryLaneGuidance.Apply([mainAction],[sideBirth,scene.Route[1],mainExit],empty,index));
        Assert.Equal(left?ManeuverKind.MergeRight:ManeuverKind.MergeLeft,required.Kind);
        Assert.Equal(200,required.EndDistanceMeters!.Value-required.DistanceMeters,5);
        Assert.DoesNotContain(AuxiliaryLaneGuidance.Apply([scene.Action],[sideBirth,..scene.Route.Skip(1)],empty,index),a=>a.Id==required.Id);
        var middle=new LaneNavigationPlan([],[],true,null){InitialTransfer=new("road",mainExit.Id,[scene.Route[1].Id],left?0:2,1,false){AllowedLaneChange=true}};
        var midway=Assert.Single(AuxiliaryLaneGuidance.Apply([mainAction],[scene.Route[1],mainExit],middle,index));
        Assert.Equal(required.Kind,midway.Kind);Assert.Same(immutable,index.Corridors);
        Assert.Equal(required.ApproachInterval!.LengthMeters,midway.ApproachInterval!.LengthMeters,5);
        var alreadyMain=new LaneNavigationPlan([new(mainExit.Id,mainExit.LanePaths[0])],[],true,null);
        Assert.Null(alreadyMain.InitialTransfer);
        var silentMiddle=Assert.Single(AuxiliaryLaneGuidance.Apply([mainAction],[scene.Route[1],mainExit],alreadyMain,index));
        Assert.Equal(NavigationAnnouncementMode.Silent,silentMiddle.AnnouncementMode);
        Assert.All(corridor.Sections,s=>Assert.Same(scene.Graph.Single(e=>e.Id==s.Edge.Id),s.Edge));
    }

    [Fact]
    public void MissingAdjacentStructureIsRetriedByTheNextMapVersion()
    {
        var scene=Corridor([0,1],[2],[2],168.92394f);
        var partial=AuxiliaryLaneIndex.Build(new NativeLaneGraph(scene.Graph.Where(e=>!e.Id.StartsWith("prefab:2:")).ToArray()),TestContext.Current.CancellationToken);
        Assert.Empty(partial.Corridors);Assert.Contains(partial.Diagnostics,d=>d.Contains("尚未加载"));
        var complete=AuxiliaryLaneIndex.Build(new NativeLaneGraph(scene.Graph),TestContext.Current.CancellationToken);
        Assert.Equal(168.92394,Assert.Single(complete.Corridors).LengthMeters,3);
        Assert.Empty(partial.Corridors);Assert.NotSame(partial.ByRoad,complete.ByRoad);
    }

    [Fact]
    public void UnknownMainPriorityDoesNotErasePhysicalCorridorFacts()
    {
        var scene=Corridor([0,1],[2],[2]);
        var graph=scene.Graph.Select(e=>e with{JoinPriority=null}).ToArray();
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(graph),TestContext.Current.CancellationToken);
        Assert.NotEmpty(index.Corridors);Assert.All(index.Corridors,c=>Assert.Null(c.MainEntrance));
        Assert.Contains(index.Diagnostics,d=>d.Contains("主路身份尚未确定"));
        Assert.Equal(ManeuverKind.ChooseLanes,Assert.Single(AuxiliaryLaneGuidance.Apply([scene.Action],scene.Route,new([],[],true,null),index)).Kind);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void OrdinaryCrossingAndItsDedicatedTurnLaneAreNotAuxiliaryExits(bool crossingAtStart)
    {
        var scene=Corridor([0,1],[2],[2]);
        var graph=scene.Graph.Select(e=>(crossingAtStart?e.Id.StartsWith("prefab:1:"):e.Id.StartsWith("prefab:2:"))
            ?e with{Junction=new(3,3,1)}:e).ToArray();
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(graph),TestContext.Current.CancellationToken);
        Assert.Empty(index.Corridors);
        var turn=scene.Action with{Kind=ManeuverKind.TurnRight};
        Assert.Equal(turn,Assert.Single(AuxiliaryLaneGuidance.Apply([turn],scene.Route,new([],[],true,null),index)));
    }

    private sealed record Scene(DirectedRoad[] Route,DirectedRoad[] Graph,NavigationManeuver Action,double[] Offsets);
    private static Scene Corridor(int[] mainOrders,int[] addedOrders,int[] exitOrders,float length=200)
    {
        int count=mainOrders.Concat(addedOrders).Max()+1;
        PrefabLanePath Lane(ulong uid,ulong from,ulong to,int input,int output,int inputs,int outputs,
            int inputOrder,int outputOrder)=>new(uid,from,to,input,output,inputOrder,outputOrder,inputs,outputs,
                [(int)(uid*100)+input*10+output],[new(0,0,0),new(100,0,0)],100)
                {NativeBranchSignal=ForkSignal.None,CurveBlinkers=[LaneBlinker.NoBlinker],CurveStartPointIndices=[0]};
        var own=mainOrders.Select((order,i)=>Lane(1,10,11,i,order,mainOrders.Length,count,i,order)).ToArray();
        var side=addedOrders.Select((order,i)=>Lane(1,99,11,i,order,addedOrders.Length,count,i,order)).ToArray();
        var birth=new DirectedRoad("prefab:1:0:1",10,11,[new(0,0,0),new(100,0,0)])
        {
            LanePaths=own,
            LaneConnections=[..own.Select(p=>new LaneConnection(10,p.EntryLane,11,p.ExitLane)),
                ..side.Select(p=>new LaneConnection(99,p.EntryLane,11,p.ExitLane))],
            JoinPriority=new(10,[],[],20,"fixture: selected road is the proven main entrance")
        };
        var sideBirth=new DirectedRoad("prefab:1:2:1",99,11,[new(80,0,20),new(100,0,0)]){LanePaths=side};
        var road=new DirectedRoad("corridor",11,20,[new(100,0,0),new(100+length,0,0)]);
        var otherOrders=Enumerable.Range(0,count).Except(exitOrders).ToArray();
        var info=new RoadForkInfo(20,count,
            [new(21,ForkSignal.None,exitOrders,exitOrders,[],true),new(22,ForkSignal.None,otherOrders,otherOrders,[],true)]);
        var exit=new DirectedRoad("prefab:2:0:1",20,21,[new(100+length,0,0),new(150+length,0,20)])
            {LanePaths=exitOrders.Select((order,i)=>Lane(2,20,21,order,i,count,exitOrders.Length,order,i)).ToArray(),ForkInfo=info};
        var mainExit=new DirectedRoad("prefab:2:0:2",20,22,[new(100+length,0,0),new(200+length,0,0)])
            {LanePaths=otherOrders.Select((order,i)=>Lane(2,20,22,order,i,count,otherOrders.Length,order,i)).ToArray(),ForkInfo=info};
        var instruction=RoadForkRules.Decide(info,21);
        return new([birth,road,exit],[birth,sideBirth,road,exit,mainExit],
            new("fork",instruction.Kind,100+length,"in","out")
            {JunctionId="prefab:2",Instruction=instruction.Text,ForkInstruction=instruction},[0,100,100+length,150+length]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedEntranceAndExitLaneHasStructuralMovementWithoutChangingNativeLamps(bool left)
    {
        var scene=left?Corridor([1,2],[0],[0]):Corridor([0,1],[2],[2]);
        Assert.Equal(ManeuverKind.ChooseLanes,scene.Action.Kind);
        var before=scene.Graph.SelectMany(e=>e.LanePaths).Select(p=>(p.NativeBranchSignal,p.CurveBlinkers)).ToArray();
        var proof=DedicatedLaneOpening.Find(scene.Route,scene.Graph,scene.Offsets,2,scene.Action.ForkInstruction!.DedicatedOrders)!;
        Assert.Null(proof.Signal);Assert.Equal(left,proof.MoveLeft);
        var updated=Assert.Single(new LaneExitApproach().Apply([scene.Action],scene.Route,scene.Graph,0));
        Assert.Equal(left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight,updated.Kind);
        Assert.Null(updated.Instruction);Assert.Equal(120,updated.DistanceMeters);Assert.Equal(300,updated.EndDistanceMeters);
        Assert.Equal(new LaneActionInterval(100,300),updated.ApproachInterval);
        Assert.Equal(before,scene.Graph.SelectMany(e=>e.LanePaths).Select(p=>(p.NativeBranchSignal,p.CurveBlinkers)).ToArray());
        Assert.All(scene.Graph.SelectMany(e=>e.LanePaths),p=>Assert.All(p.CurveBlinkers,b=>Assert.Equal(LaneBlinker.NoBlinker,b)));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void MultipleAuxiliaryLanesRequireOneSharedDirection(bool left)
    {
        var scene=left?Corridor([2,3],[0,1],[0,1]):Corridor([0,1],[2,3],[2,3]);
        var updated=Assert.Single(new LaneExitApproach().Apply([scene.Action],scene.Route,scene.Graph,0));
        Assert.Equal(left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight,updated.Kind);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void MixedOrInterleavedAuxiliaryGroupsKeepLaneSelectionAndExplainUncertainty(bool mixed)
    {
        var scene=mixed?Corridor([1,2],[0,3],[0,3]):Corridor([0,2],[1],[1]);
        var detector=new LaneExitApproach();
        var updated=Assert.Single(detector.Apply([scene.Action],scene.Route,scene.Graph,0));
        Assert.Equal(ManeuverKind.ChooseLanes,updated.Kind);
        Assert.Contains(detector.Diagnostics,d=>d.Reason.Contains("无法得到唯一横向方向"));
        detector.Reset();Assert.Empty(detector.Diagnostics);
    }

    [Fact]
    public void PermanentUnlitForkWithoutAuxiliaryBirthKeepsLaneSelection()
    {
        var scene=Corridor([0,1,2,3],[],[2,3]);var detector=new LaneExitApproach();
        var updated=Assert.Single(detector.Apply([scene.Action],scene.Route,scene.Graph,0));
        Assert.Equal(ManeuverKind.ChooseLanes,updated.Kind);Assert.Equal(scene.Action.DistanceMeters,updated.DistanceMeters);
        Assert.Null(updated.ApproachInterval);Assert.Empty(detector.Diagnostics);
    }

    [Fact]
    public void ALongParallelLaneIsNotReclassifiedAsAnAuxiliaryExit()
    {
        var scene=Corridor([0,1],[2],[2],500.25f);
        var updated=Assert.Single(new LaneExitApproach().Apply([scene.Action],scene.Route,scene.Graph,0));
        Assert.Equal(ManeuverKind.ChooseLanes,updated.Kind);Assert.Null(updated.ApproachInterval);
    }

    [Fact]
    public void AlongMainRoadTheProvenAuxiliaryExitDoesNotProduceLaneAdvice()
    {
        var scene=Corridor([0,1],[2],[2]);var main=scene.Graph.Single(e=>e.Id=="prefab:2:0:2");
        var instruction=RoadForkRules.Decide(main.ForkInfo!,22);
        var action=scene.Action with{ForkInstruction=instruction,Instruction=instruction.Text};
        var updated=Assert.Single(new LaneExitApproach().Apply([action],[..scene.Route.Take(2),main],scene.Graph,0));
        Assert.Equal(ManeuverKind.Straight,updated.Kind);Assert.Equal(NavigationAnnouncementMode.Silent,updated.AnnouncementMode);
    }

    [Fact]
    public void ResetDiscardsProofWhileOrdinaryPrefixTrimmingPreservesIt()
    {
        var scene=Corridor([0,1],[2],[2]);var detector=new LaneExitApproach();
        Assert.Equal(ManeuverKind.KeepRight,Assert.Single(detector.Apply([scene.Action],scene.Route,scene.Graph,0)).Kind);
        var trimmed=scene.Route.Skip(1).ToArray();
        var retained=Assert.Single(detector.Apply([scene.Action],trimmed,scene.Graph,0));
        Assert.Equal(ManeuverKind.KeepRight,retained.Kind);Assert.NotNull(retained.ApproachInterval);
        detector.Reset();
        var fresh=Assert.Single(detector.Apply([scene.Action],trimmed,scene.Graph,0));
        Assert.Equal(ManeuverKind.ChooseLanes,fresh.Kind);Assert.Null(fresh.ApproachInterval);
    }

    [Fact]
    public void ChangingOnlyDirectionKeepsExistingTriggerIdentityAndConsumption()
    {
        var scene=Corridor([0,1],[2],[2]);
        var updated=Assert.Single(new LaneExitApproach().Apply([scene.Action],scene.Route,scene.Graph,0));
        var previous=updated with{Kind=ManeuverKind.ChooseLanes,Instruction=scene.Action.Instruction,ForkInstruction=scene.Action.ForkInstruction};
        var now=DateTimeOffset.UtcNow;var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var oldPlan=planner.Build("session","route",now,0,[previous],true,false,false,scheduler,"frame");
        timeline.Publish(oldPlan);scheduler.Update(timeline,now,10);
        var refined=planner.Build("session","route",now,20,[updated with{DistanceMeters=updated.DistanceMeters-20,EndDistanceMeters=updated.EndDistanceMeters-20}],true,false,false,scheduler,"frame");
        Assert.Equal(oldPlan.Events[0].Id,refined.Events[0].Id);Assert.Equal(oldPlan.Events[0].AlongMeters,refined.Events[0].AlongMeters);
        Assert.Equal(oldPlan.Sequences[0].EndAlongMeters,refined.Sequences[0].EndAlongMeters);
        timeline.Publish(refined);
        Assert.DoesNotContain(scheduler.Update(timeline,now,10),r=>r.Disposition==TriggerDisposition.Queued&&r.Trigger.Kind==SpeechTriggerKind.Overview);
    }
}
