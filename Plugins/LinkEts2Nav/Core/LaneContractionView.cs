using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Plans entrance-owned required joins from complete native side-lane intervals, without editing map connectivity.</summary>
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
        var result=new List<Required>();
        foreach(var corridor in auxiliaries.Corridors.Where(c=>c.StartKind==AuxiliaryBoundaryKind.IndependentEntrance
            &&c.MainEntrance is not null&&c.SideIsLeft is not null))
        foreach(var terminal in corridor.Terminals.Where(t=>(t.MainOnly||t.Merges)&&!t.DirectionConflict))
        {
            if(LaneCorridorRoute.Find(route,terminal.Edge)<0)continue;
            if(!terminal.Merges&&!corridor.Terminals.Any(t=>t.SideOnly&&LaneCorridorRoute.Find(route,t.Edge)<0))continue;
            var all=auxiliaries.Graph.InPrefab(terminal.Edge).SelectMany(e=>e.LanePaths)
                .Where(p=>p.EntryNodeUid==terminal.Edge.StartNodeUid).ToArray();
            var side=all.Where(p=>terminal.SideOrders.Contains(p.EntryOrder)
                &&(terminal.Merges||p.ExitNodeUid!=terminal.Edge.EndNodeUid)).ToArray();
            var kept=terminal.Edge.LanePaths.Where(p=>terminal.MainOrders.Contains(p.EntryOrder)).ToArray();
            var possible=(from a in side from b in kept
                where !terminal.Merges||SharesTail(a,b)
                orderby Math.Abs(a.EntryOrder-b.EntryOrder) select(A:a,B:b)).ToArray();
            if(possible.Length==0)continue;
            var pair=possible[0];
            bool adjacent=!corridor.Sections.Any(s=>NativeLaneGraph.Prefab(s.Edge) is null);
            bool permitted=terminal.Merges||corridor.Origin.LanePaths.Where(p=>corridor.OriginSideOrders.Contains(p.ExitOrder))
                .Any(p=>LaneRouteConnections.Between(p,pair.B,adjacent) is {Possible:true,Allowed:true});
            if(!permitted)continue;
            var interval=LaneCorridorRoute.Interval(route,offsets,corridor,terminal);if(interval is null)continue;
            bool left=terminal.NativeMoveLeft??!corridor.SideIsLeft!.Value;
            ulong prefab=corridor.Origin.LanePaths[0].PrefabUid;
            var merge=new LaneMergeObservation("entrance-merge:"+corridor.Id+":"+terminal.Edge.Id,prefab,
                corridor.Origin.StartNodeUid,pair.A.EntryLane,corridor.MainEntrance,pair.B.EntryLane,
                new LanePolyline(terminal.Edge.Points).At(terminal.EndMeters),left,terminal.MainOrders.Count,
                corridor.LengthMeters,LaneMergeKind.Ramp,interval.EndMeters,"Native entrance and complete side-lane end within 500 m")
                {EndDistanceMeters=LaneCorridorRoute.PassageEnd(route,offsets,terminal.Edge),ApproachInterval=interval,EntranceBased=true,
                    CompletionJunctionId="prefab:"+NativeLaneGraph.Prefab(terminal.Edge),
                    ApproachLaneCount=pair.A.EntryLaneCount,JunctionId="prefab:"+NativeLaneGraph.Prefab(corridor.Origin)};
            result.Add(new(merge,corridor));
        }
        return new(route,auxiliaries,result.ToArray(),offsets);
    }

    private static bool SharesTail(PrefabLanePath side,PrefabLanePath main)
    {
        if(side.ExitNodeUid!=main.ExitNodeUid||side.ExitLane!=main.ExitLane)return false;
        int common=0;while(common<Math.Min(side.CurveIndices.Count,main.CurveIndices.Count)
            &&side.CurveIndices[^(common+1)]==main.CurveIndices[^(common+1)])common++;
        return common>0&&MergeBlinkerDirection.Classify(side,main,common).FirstEnds==true;
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

/// <summary>Physical same-source reductions are environment warnings; required entrance joins are navigation actions.</summary>
public static class LaneContractionGuidance
{
    public static IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,IReadOnlyList<LaneMergeObservation> merges)
    {
        // Generic prefab entrance roles are not physical contraction positions.
        var result=actions.Where(a=>!a.Id.StartsWith("merge:",StringComparison.Ordinal)).ToList();
        // Entrance/exit navigation belongs exclusively to ComponentContinuity.
        // Only actual same-source lane reductions remain environment events.
        foreach(var merge in merges.Where(m=>m.Kind==LaneMergeKind.LaneReduction)
            .DistinctBy(m=>(m.PrefabUid,m.Position,m.Origin?.ApplicableEntryNode,
                Orders:string.Join(',',m.Origin?.ApplicableEntryLanes??[]))))
        {
            string group=merge.JunctionId??$"prefab:{merge.PrefabUid:X16}";
            var action=new NavigationManeuver(merge.Id,merge.MoveLeft==false?ManeuverKind.MergeRight:ManeuverKind.MergeLeft,
                merge.DistanceMeters,"lane","lane")
                {EndDistanceMeters=merge.EndDistanceMeters,JunctionId=group,JunctionKind=NavigationJunctionKind.Ramp,
                    HasSeparateApproachRoad=false,ApproachInterval=merge.ApproachInterval};
            string? selection=merge.ApproachLaneCount is {} count
                ?LaneInstructionFormatter.Selection(count,merge.SurvivingEntryOrders):null;
            action=action with{AnnouncementMode=NavigationAnnouncementMode.DistancePreview,
                Instruction=selection is null?"车道合并，请注意观察":"车道合并，建议提前"+selection};
            result.Add(action);
        }
        return result.OrderBy(m=>m.DistanceMeters).ToArray();
    }
    // The real exit identifies this visit even when its cue was advanced or later clamped.
    // This tolerance only absorbs coordinate arithmetic rounding, never an approach-distance heuristic.
    private static bool SamePassageEnd(double? actionEnd,double? joinEnd)
        =>actionEnd is {} a&&joinEnd is {} b&&Math.Abs(a-b)<=.001;
}
