using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class AuxiliaryLaneDirectionTests
{
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
