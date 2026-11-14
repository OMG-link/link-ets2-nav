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
    public AuxiliaryLaneIndex? Auxiliaries {get;init;}
}

/// <summary>Runs only when route topology or loaded map facts change.</summary>
internal sealed class NavigationStructurePlanner(IReadOnlySet<string>? spokenPlaceNames=null,Action<string>? log=null)
{
    private readonly NavigationManeuverDetector detector=new();
    private readonly LaneMergeDetector mergeDetector=new();

    /// <summary>Clear transient lane selection state; physical corridor facts belong to the immutable map snapshot.</summary>
    public void Reset()
    {
        mergeDetector.Reset();
    }
    public NavigationStructurePlan Build(IReadOnlyList<DirectedRoad> route,RouteSnapshot sdkRoute,NearbyMapSnapshot nearby,RoadPathSource paths,LaneNavigationPlan lanes,AuxiliaryLaneIndex auxiliaries)
    {
        var instructionRoute=RoundaboutRouteAdapter.Apply(route,nearby.RoadStructures);
        IReadOnlyList<NavigationManeuver> upcoming=detector.FindSequence(instructionRoute,0,paths.GetNavigationGraph(),out double confirmedUntil,out bool unresolvedJoin,out double firstUnresolvedJoinAlong);
        if(NavigationDestination.Find(sdkRoute,instructionRoute,0,confirmedUntil,unresolvedJoin) is {} destination)
        {
            var terminal=sdkRoute.Nodes.MinBy(n=>n.DistanceToEndMeters)!;
            if(nearby.Companies.AtTerminal(terminal.Uid) is {DisplayName:{} name})
            {
                destination=destination with{DestinationCompanyName=name,ExecutionInstruction="到达"+name+"货场附近，本次导航结束"};
                log?.Invoke("导航终点已按预制件归属确认货场名称："+name+
                    (spokenPlaceNames?.Contains(name)==true?"；名称音频已准备。":"；缺少该名称的预生成音频，播放时使用通用目的地提示。"));
            }
            upcoming=upcoming.Append(destination).OrderBy(m=>m.DistanceMeters).ToArray();
        }
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
        var plan=new NavigationStructurePlan(route,instructionRoute,upcoming,lanes,[],detector.ForkDiagnostics.ToArray(),confirmedUntil,
            unresolvedJoin,firstUnresolvedJoinAlong){BaseActions=upcoming,Auxiliaries=auxiliaries};
        return SelectLanes(plan,lanes,nearby,paths);
    }

    /// <summary>Selection changes reuse all junction/ramp facts; only already indexed lane-merge membership is selected again.</summary>
    public NavigationStructurePlan SelectLanes(NavigationStructurePlan plan,LaneNavigationPlan lanes,NearbyMapSnapshot nearby,RoadPathSource paths)
    {
        IReadOnlyList<NavigationManeuver> upcoming=plan.Auxiliaries is {} auxiliaries
            ?AuxiliaryLaneGuidance.Apply(plan.BaseActions,plan.InstructionRoute,lanes,auxiliaries):plan.BaseActions;
        var merges=mergeDetector.Find(plan.Route,paths.GetNavigationGraph(),lanes,0)
            .Where(m=>!nearby.RoadStructures.ContainsKey(m.PrefabUid))
            .Select(m=>plan.Auxiliaries?.NativeMerge(m) is {} native?m with{Kind=LaneMergeKind.Ramp,
                ParallelDistanceMeters=native.LengthMeters,Reason="已由加载地图中的原生辅助车道走廊证明完整区间"}:m).ToArray();
        var issues=plan.Forks.ToList();
        foreach(var merge in merges)
        {
            string group=$"prefab:{merge.PrefabUid:X16}";
            // Ramp actions come exclusively from the immutable auxiliary-corridor index.
            // The merge detector remains the source of ordinary lane-reduction environment events.
            if(merge.Kind!=LaneMergeKind.LaneReduction)
            {
                if(merge.Kind==LaneMergeKind.Ramp&&plan.Auxiliaries?.NativeMerge(merge) is null
                    &&!upcoming.Any(a=>a.JunctionId==group&&a.ApproachInterval is not null))
                    issues.Add(new(group,merge.EndingEntryNode??0,0,"已见原生合流，但加载地图尚未证明完整辅助区间；不再从玩家路线/短距离几何猜测区间起点"));
                continue;
            }
            if(merge.MoveLeft is null)continue;
            string side=merge.MoveLeft==true?"左":"右";
            string laneWords=merge.RecommendedLaneCount==1?"车道":(merge.RecommendedLaneCount==2?"两":NavigationPrompts.Number(merge.RecommendedLaneCount))+"条车道";
            double anchor=merge.DistanceMeters;
            var action=new NavigationManeuver(merge.Id,merge.MoveLeft==true?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,anchor,"lane","lane")
            { EndDistanceMeters=merge.EndDistanceMeters,JunctionKind=NavigationJunctionKind.Ramp,JunctionId="prefab:"+merge.PrefabUid.ToString("X16"),HasSeparateApproachRoad=false,
                AnnouncementMode=NavigationAnnouncementMode.DistancePreview,
                Instruction=$"车道合并，建议提前走{side}侧{laneWords}" };
            upcoming=upcoming.Append(action).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        return plan with{Actions=upcoming,Lanes=lanes,Merges=merges,Forks=issues.Distinct().ToArray()};
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
