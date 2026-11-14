using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Runtime;

/// <summary>Immutable route facts in one component-local coordinate system. No vehicle progress is stored here.</summary>
internal sealed record NavigationStructurePlan(IReadOnlyList<DirectedRoad> Route,IReadOnlyList<DirectedRoad> InstructionRoute,
    IReadOnlyList<NavigationManeuver> Actions,LaneNavigationPlan Lanes,IReadOnlyList<LaneMergeObservation> Merges,
    IReadOnlyList<RoadForkDiagnostic> Forks,double ConfirmedUntil,bool Unresolved,double UnresolvedAlong)
{
    public IReadOnlyList<NavigationManeuver> BaseActions {get;init;}=[];
}

/// <summary>Runs only when route topology or loaded map facts change.</summary>
internal sealed class NavigationStructurePlanner(IReadOnlySet<string>? spokenPlaceNames=null)
{
    private readonly NavigationManeuverDetector detector=new();
    private readonly LaneMergeDetector mergeDetector=new();
    private readonly AccelerationLaneApproach acceleration=new();
    private readonly LaneExitApproach exitApproach=new();
    public NavigationStructurePlan Build(IReadOnlyList<DirectedRoad> route,RouteSnapshot sdkRoute,NearbyMapSnapshot nearby,RoadPathSource paths,LaneNavigationPlan lanes)
    {
        var instructionRoute=RoundaboutRouteAdapter.Apply(route,nearby.RoadStructures);
        IReadOnlyList<NavigationManeuver> upcoming=detector.FindSequence(instructionRoute,0,paths.GetNavigationGraph(),out double confirmedUntil,out bool unresolvedJoin,out double firstUnresolvedJoinAlong);
        var accelerationActions=acceleration.Find(instructionRoute,paths.GetNavigationGraph(),0);
        upcoming=upcoming.Where(m=>!accelerationActions.Any(a=>m.JunctionId==a.JunctionId))
            .Concat(accelerationActions).OrderBy(m=>m.DistanceMeters).ToArray();
        if(NavigationDestination.Find(sdkRoute,instructionRoute,0,confirmedUntil,unresolvedJoin) is {} destination)
            upcoming=upcoming.Append(destination).OrderBy(m=>m.DistanceMeters).ToArray();
        foreach(var leg in nearby.Ferries?.OnRoute(sdkRoute)??[])
        {
            double offset=0;double? entry=null;
            foreach(var edge in route)
            {
                if(edge.StartNodeUid==leg.EntranceUid){entry=offset;break;}
                offset+=RouteGeometry.Length(edge);
                if(edge.EndNodeUid==leg.EntranceUid){entry=offset;break;}
            }
            if(entry is not {} at)continue;
            upcoming=upcoming.Append(new NavigationManeuver(leg.Id,ManeuverKind.BoardFerry,at,route[^1].Id,route[^1].Id)
            {
                EndDistanceMeters=at,JunctionId="prefab:"+leg.Source.PrefabUid.ToString("X16"),
                Instruction="进入码头",NearInstruction=FerryNavigationPrompts.Passage(leg.Source.Name,leg.Target.Name,spokenPlaceNames)
            }).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        upcoming=exitApproach.Apply(upcoming,instructionRoute,paths.GetNavigationGraph(),0);
        var plan=new NavigationStructurePlan(route,instructionRoute,upcoming,lanes,[],detector.ForkDiagnostics.ToArray(),confirmedUntil,
            unresolvedJoin,firstUnresolvedJoinAlong){BaseActions=upcoming};
        return SelectLanes(plan,lanes,nearby,paths);
    }

    /// <summary>Selection changes reuse all junction/ramp facts; only already indexed lane-merge membership is selected again.</summary>
    public NavigationStructurePlan SelectLanes(NavigationStructurePlan plan,LaneNavigationPlan lanes,NearbyMapSnapshot nearby,RoadPathSource paths)
    {
        IReadOnlyList<NavigationManeuver> upcoming=plan.BaseActions;
        var merges=mergeDetector.Find(plan.Route,paths.GetNavigationGraph(),lanes,0)
            .Where(m=>!nearby.RoadStructures.ContainsKey(m.PrefabUid)).ToArray();
        NavigationManeuver? ProvenAcceleration(LaneMergeObservation m)=>plan.BaseActions.FirstOrDefault(a=>
            a.Kind is ManeuverKind.MergeLeft or ManeuverKind.MergeRight&&a.JunctionId=="prefab:"+m.PrefabUid.ToString("X16")&&m.MoveLeft is {} left
            &&a.Kind==(left?ManeuverKind.MergeLeft:ManeuverKind.MergeRight)&&a.EndDistanceMeters is {} end&&Math.Abs(end-m.DistanceMeters)<2);
        merges=merges.Select(m=>ProvenAcceleration(m) is {} proof?m with{Kind=LaneMergeKind.Ramp,
            ParallelDistanceMeters=proof.ApproachInterval!.LengthMeters,Reason="已沿原生车道继承证明独立入口、并行及最终收口完整区间"}:m).ToArray();
        foreach(var merge in merges)
        {
            string group=$"prefab:{merge.PrefabUid:X16}";
            var accelerationProof=ProvenAcceleration(merge);
            bool replacesNavigation=accelerationProof is not null||merge.Kind==LaneMergeKind.Ramp&&merge.MoveLeft is not null;
            if(replacesNavigation)
                upcoming=upcoming.Where(m=>m.JunctionId!=group||m.Kind is ManeuverKind.TurnLeft or ManeuverKind.TurnRight or ManeuverKind.UTurn or ManeuverKind.EnterRoundabout or ManeuverKind.ExitRoundabout or ManeuverKind.PassRoundaboutExit).ToArray();
            if(accelerationProof is not null)continue;
            if(merge.MoveLeft is null)continue;
            string side=merge.MoveLeft==true?"左":"右";
            string laneWords=merge.RecommendedLaneCount==1?"车道":(merge.RecommendedLaneCount==2?"两":NavigationPrompts.Number(merge.RecommendedLaneCount))+"条车道";
            double anchor=merge.Kind==LaneMergeKind.Ramp&&merge.ParallelDistanceMeters is {} length?new LaneActionInterval(merge.DistanceMeters-length,merge.DistanceMeters).AtFraction(.1):merge.DistanceMeters;
            var action=new NavigationManeuver(merge.Id,merge.MoveLeft==true?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,anchor,"lane","lane")
            { EndDistanceMeters=merge.EndDistanceMeters,JunctionKind=NavigationJunctionKind.Ramp,JunctionId="prefab:"+merge.PrefabUid.ToString("X16"),HasSeparateApproachRoad=false,
                AnnouncementMode=merge.Kind==LaneMergeKind.LaneReduction?NavigationAnnouncementMode.DistancePreview:merge.Kind==LaneMergeKind.Ramp?NavigationAnnouncementMode.Normal:NavigationAnnouncementMode.Silent,
                ApproachInterval=merge.Kind==LaneMergeKind.Ramp&&merge.ParallelDistanceMeters is {} span?new(merge.DistanceMeters-span,merge.DistanceMeters):null,
                Instruction=merge.Kind==LaneMergeKind.LaneReduction?$"车道合并，建议提前走{side}侧{laneWords}":null };
            upcoming=upcoming.Append(action).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        return plan with{Actions=upcoming,Lanes=lanes,Merges=merges};
    }

    public static bool SelectionChanged(LaneNavigationPlan before,LaneNavigationPlan after)
    {
        foreach(var old in before.Sections)
        {
            var current=after.Sections.FirstOrDefault(s=>s.EdgeId==old.EdgeId);
            if(current is not null&&(old.Path.EntryOrder!=current.Path.EntryOrder||old.Path.ExitOrder!=current.Path.ExitOrder
                ||!old.Path.CurveIndices.SequenceEqual(current.Path.CurveIndices)))return true;
        }
        // Removing driven layers is ordinary progress, not a different selected path.
        return before.Sections.FirstOrDefault()?.EdgeId==after.Sections.FirstOrDefault()?.EdgeId
            &&before.InitialTransfer?.FromLane!=after.InitialTransfer?.FromLane;
    }
}
