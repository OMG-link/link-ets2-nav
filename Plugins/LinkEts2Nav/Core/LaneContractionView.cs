using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Route-owned feasibility overlay. Native graph objects and cached map corridors are never edited.</summary>
public sealed class LaneContractionView
{
    private sealed record Required(LaneMergeObservation Merge,AuxiliaryLaneCorridor Corridor);
    private readonly IReadOnlyList<DirectedRoad> route;
    private readonly AuxiliaryLaneIndex auxiliaries;
    private readonly Required[] required;
    private readonly double[] offsets;
    private LaneContractionView(IReadOnlyList<DirectedRoad> route,AuxiliaryLaneIndex auxiliaries,Required[] required,double[] offsets)
    {this.route=route;this.auxiliaries=auxiliaries;this.required=required;this.offsets=offsets;}

    public static LaneContractionView Build(IReadOnlyList<DirectedRoad> route,AuxiliaryLaneIndex auxiliaries)
    {
        double[] offsets=new double[route.Count+1];for(int i=0;i<route.Count;i++)offsets[i+1]=offsets[i]+RouteGeometry.Length(route[i]);
        var result=new List<Required>();var origins=new LaneMergeOriginClassifier(auxiliaries.Graph.ThroughTraffic);
        foreach(var corridor in auxiliaries.ParallelCorridors)
        foreach(var terminal in corridor.Terminals.Where(t=>t.MainOnly&&!t.Merges&&!t.DirectionConflict))
        {
            int at=LaneCorridorRoute.Find(route,terminal.Edge);if(at<0||corridor.SideIsLeft is null)continue;
            if(!corridor.Terminals.Any(t=>t.SideOnly&&LaneCorridorRoute.Find(route,t.Edge)<0))continue;
            var all=auxiliaries.Graph.InPrefab(terminal.Edge).SelectMany(e=>e.LanePaths)
                .Where(p=>p.EntryNodeUid==terminal.Edge.StartNodeUid).ToArray();
            var side=all.Where(p=>terminal.SideOrders.Contains(p.EntryOrder)&&p.ExitNodeUid!=terminal.Edge.EndNodeUid).ToArray();
            var kept=terminal.Edge.LanePaths.Where(p=>terminal.MainOrders.Contains(p.EntryOrder)).ToArray();
            var possible=(from a in side from b in kept orderby Math.Abs(a.EntryOrder-b.EntryOrder) select(A:a,B:b)).ToArray();
            if(possible.Length==0)continue;
            var pair=possible[0];
            bool adjacent=!corridor.Sections.Any(s=>NativeLaneGraph.Prefab(s.Edge) is null);
            bool permitted=corridor.Origin.LanePaths.Where(p=>corridor.OriginSideOrders.Contains(p.ExitOrder))
                .Any(p=>LaneRouteConnections.Between(p,pair.B,adjacent) is {Possible:true,Allowed:true});
            if(!permitted)continue; // Removing an outlet alone never invents a crossing or lane-change connection.
            var interval=LaneCorridorRoute.Interval(route,offsets,corridor,terminal);if(interval is null)continue;
            var point=new LanePolyline(terminal.Edge.Points).At(terminal.EndMeters);
            var origin=origins.Classify(pair.A,new LanePolyline(pair.A.Points).Project(point).Along,
                pair.B,new LanePolyline(pair.B.Points).Project(point).Along);
            bool left=pair.A.EntryOrder>pair.B.EntryOrder;
            int count=terminal.MainOrders.Count(n=>left?n<pair.A.EntryOrder:n>pair.A.EntryOrder);
            var merge=new LaneMergeObservation("route-contraction:"+corridor.Id+":"+terminal.Edge.Id,pair.A.PrefabUid,
                pair.A.EntryNodeUid,pair.A.EntryLane,pair.B.EntryNodeUid,pair.B.EntryLane,point,left,Math.Max(1,count),
                corridor.LengthMeters,origin.Kind,interval.EndMeters,origin.Reason)
                {EndDistanceMeters=interval.EndMeters,ApproachInterval=interval,Origin=origin,RouteConstrained=true,ApproachLaneCount=pair.A.EntryLaneCount,JunctionId="prefab:"+NativeLaneGraph.Prefab(terminal.Edge)};
            result.Add(new(merge,corridor));
        }
        return new(route,auxiliaries,result.ToArray(),offsets);
    }

    public IReadOnlyList<LaneMergeObservation> Select(IReadOnlyList<LaneMergeObservation> physical,LaneNavigationPlan lanes)
    {
        var result=physical.Select(m=>
        {
            var corridor=auxiliaries.ParallelMerge(m);var terminal=corridor?.Terminals.FirstOrDefault(t=>t.Merges&&t.Edge.LanePaths.Any(p=>p.PrefabUid==m.PrefabUid));
            var interval=terminal is null?null:LaneCorridorRoute.Interval(route,offsets,corridor!,terminal);
            return interval is null?m:m with{DistanceMeters=interval.EndMeters,EndDistanceMeters=interval.EndMeters,ApproachInterval=interval};
        }).ToList();
        foreach(var candidate in required)
        {
            var corridor=candidate.Corridor;
            var origin=lanes.Sections.FirstOrDefault(s=>s.EdgeId==corridor.Origin.Id)?.Path;
            bool ending=corridor.StartKind==AuxiliaryBoundaryKind.IndependentEntrance
                ?route.Any(e=>e.Id==corridor.Origin.Id):origin is {} p&&corridor.OriginSideOrders.Contains(p.ExitOrder);
            if(!ending&&origin is null)
            {
                var first=corridor.Sections.FirstOrDefault(s=>LaneCorridorRoute.Find(route,s.Edge)>=0);
                int? lane=lanes.InitialTransfer?.FromLane;
                if(lane is null&&first is not null)lane=lanes.Sections.FirstOrDefault(s=>s.EdgeId==first.Edge.Id)?.Path.EntryOrder;
                ending=lane is {} order&&first?.SideOrders.Contains(order)==true;
            }
            if(ending)result.Add(candidate.Merge);
        }
        return result.DistinctBy(m=>m.Id).OrderBy(m=>m.DistanceMeters).ToArray();
    }


}

/// <summary>One instruction policy for physical and goal-constrained contraction candidates.</summary>
public static class LaneContractionGuidance
{
    public static IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,IReadOnlyList<LaneMergeObservation> merges)
    {
        // Generic prefab entrance roles are not physical contraction positions.
        var result=actions.Where(a=>!a.Id.StartsWith("merge:",StringComparison.Ordinal)).ToList();
        foreach(var merge in merges.Where(m=>m.MoveLeft is not null&&m.Kind!=LaneMergeKind.Uncertain))
        {
            bool left=merge.MoveLeft!.Value;string group=merge.JunctionId??$"prefab:{merge.PrefabUid:X16}";
            result.RemoveAll(a=>a.JunctionId==group&&a.Kind is ManeuverKind.ChooseLanes or ManeuverKind.KeepLeft or ManeuverKind.KeepRight);
            var action=new NavigationManeuver(merge.Id,left?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,
                merge.Kind==LaneMergeKind.Ramp&&merge.ApproachInterval is {} interval?AuxiliaryLaneTiming.ActionPoint(interval):merge.DistanceMeters,"lane","lane")
                {EndDistanceMeters=merge.EndDistanceMeters,JunctionId=group,JunctionKind=NavigationJunctionKind.Ramp,
                    HasSeparateApproachRoad=false,ApproachInterval=merge.ApproachInterval};
            if(merge.Kind==LaneMergeKind.LaneReduction)
            {
                string? selection=LaneInstructionFormatter.EdgeSelection(merge.ApproachLaneCount,left?NavigationSide.Left:NavigationSide.Right,merge.RecommendedLaneCount);
                if(selection is null)continue;
                action=action with{AnnouncementMode=NavigationAnnouncementMode.DistancePreview,
                    Instruction="车道合并，建议提前"+selection};
            }
            result.Add(action);
        }
        return result.OrderBy(m=>m.DistanceMeters).ToArray();
    }
}
