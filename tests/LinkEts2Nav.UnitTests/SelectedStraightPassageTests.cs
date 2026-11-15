using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class SelectedStraightPassageTests
{
    // Recorded topology: one outlet is Left; another has Right from lane 0
    // and NoBlinker from lane 1. The aggregate outlet signal is unknown.
    private static NavigationStructurePlan Build(ForkSignal? selectedSignal=ForkSignal.None,
        ForkSignal? sideSignal=ForkSignal.Left,bool legalSide=true,
        NavigationAnnouncementMode targetMode=NavigationAnnouncementMode.Normal)
    {
        Vector3[] through=[new(0,0,0),new(0,0,60)];Vector3[] left=[new(0,0,0),new(-20,0,60)];
        var chosen=new PrefabLanePath(42,10,20,1,1,1,1,2,2,[1,0],through,60){NativeBranchSignal=selectedSignal};
        var entryOther=chosen with{EntryLane=0,EntryOrder=0,ExitLane=0,ExitOrder=0,NativeBranchSignal=ForkSignal.Right};
        var branch=new PrefabLanePath(42,10,30,0,0,0,0,2,1,[5,2,4,6],left,65){NativeBranchSignal=sideSignal};
        var info=new RoadForkInfo(10,2,[new(20,null,[0,1],[0,1],[],true,"mixed outlet signals"),new(30,sideSignal,[0],[0],[],false)]);
        var main=new DirectedRoad("prefab:000000000000002A:0:2",10,20,through)
            {LanePaths=[entryOther,chosen],ForkInfo=info,Junction=new(1,2,0){InterfaceCount=3}};
        var side=new DirectedRoad("prefab:000000000000002A:0:1",10,30,left){LanePaths=[branch],ForkInfo=info,Junction=main.Junction};
        DirectedRoad[] route=[main];DirectedRoad[] graph=legalSide?[main,side]:[main];
        var lanes=new LaneNavigationPlan([new(main.Id,chosen)],[],true,null);
        var target=new NavigationManeuver("merge",ManeuverKind.MergeLeft,400,"a","b")
            {EndDistanceMeters=450,JunctionId="merge-target",AnnouncementMode=targetMode};
        var source=new NavigationStructurePlan(route,route,[target],lanes,[],[new("42",10,20,"mixed outlet signals")],500,true,0)
            {BaseActions=[target]};
        var paths=new RoadPathSource(new TruckLib.ScsMap.Map(),[main],native:new NativeLaneGraph(graph));
        var nearby=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=graph};
        return new NavigationStructurePlanner().SelectLanes(source,lanes,nearby,paths);
    }
    private static GuidanceSourcePlan Speech(NavigationStructurePlan structure)
        =>new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,structure.Actions,true,false,false,new());

    [Fact]
    public void SelectedUnlitLaneRetainsPhysicalPassageAndConflictDiagnostic()
    {
        var plan=Build();var straight=Assert.Single(plan.Actions,a=>a.Kind==ManeuverKind.Straight);
        Assert.True(straight.IsMainRoadContinuation);Assert.Equal(NavigationJunctionKind.Ramp,straight.JunctionKind);
        Assert.Equal(NavigationSide.Left,straight.StraightExitSides);Assert.Equal(60,straight.EndDistanceMeters);
        Assert.Equal("mixed outlet signals",Assert.Single(plan.Forks).Reason);
        Assert.False(NavigationEventPlanner.IsNavigationAction(straight));
        Assert.Equal("直行通过当前路口。",Assert.Single(Speech(plan).Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist).Text);
    }

    [Theory]
    [InlineData(ForkSignal.Right)]
    [InlineData(null)]
    public void LitOrUnknownSelectedLaneIsNotInventedAsStraight(ForkSignal? signal)
        =>Assert.DoesNotContain(Build(signal).Actions,a=>a.Kind==ManeuverKind.Straight);

    [Fact]
    public void UnknownOrUnreachableSideExitDoesNotCreateADirectionalReminder()
    {
        Assert.DoesNotContain(Speech(Build(sideSignal:null)).Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
        Assert.DoesNotContain(Speech(Build(legalSide:false)).Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
    }

    [Fact]
    public void LaneReductionEnvironmentPreviewIsNotADirectionalNavigationTarget()
        =>Assert.DoesNotContain(Speech(Build(targetMode:NavigationAnnouncementMode.DistancePreview)).Triggers,
            t=>t.Kind==SpeechTriggerKind.ActionAssist);
}
