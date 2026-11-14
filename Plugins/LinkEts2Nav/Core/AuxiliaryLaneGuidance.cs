namespace LinkEts2Nav.Core;

/// <summary>Selects already-recognized physical corridors for an SDK route. It never discovers or caches map structure.</summary>
public static class AuxiliaryLaneGuidance
{
    public static IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,
        IReadOnlyList<DirectedRoad> route,LaneNavigationPlan lanes,AuxiliaryLaneIndex index)
    {
        var result=actions.ToList();double[] offsets=new double[route.Count+1];
        for(int i=0;i<route.Count;i++)offsets[i+1]=offsets[i]+RouteGeometry.Length(route[i]);
        var candidates=route.SelectMany(e=>index.ByRoad.GetValueOrDefault(NativeRoadKey.Of(e))??[]).DistinctBy(c=>c.Id);
        foreach(var corridor in candidates)
        {
            if(corridor.MainEntrance is null||corridor.SideIsLeft is null)continue;
            var terminal=corridor.Terminals.Select(t=>(Terminal:t,Index:Find(t.Edge)))
                .Where(t=>t.Index>=0).OrderBy(t=>t.Index).FirstOrDefault();
            if(terminal.Terminal is null)continue;
            if(terminal.Terminal.DirectionConflict)continue;
            double? start=null;
            int prior=-1;bool incomplete=false;
            foreach(var section in corridor.Sections)
            {
                int i=Find(section.Edge);
                if(i<0&&start is null)continue;
                if(i<0||i>terminal.Index||prior>=0&&i!=prior+1){incomplete=true;break;}
                start??=offsets[i]+section.StartMeters-section.CorridorAlong;prior=i;
            }
            if(start is null||incomplete)continue;
            double end=offsets[terminal.Index]+terminal.Terminal.EndMeters;
            var interval=new LaneActionInterval(start.Value,end);
            string junction="prefab:"+NativeLaneGraph.Prefab(terminal.Terminal.Edge);
            var originSelection=lanes.Sections.FirstOrDefault(s=>s.EdgeId==corridor.Origin.Id)?.Path;
            bool sideEntry=corridor.StartKind==AuxiliaryBoundaryKind.IndependentEntrance
                ?route.Any(e=>e.Id==corridor.Origin.Id)
                :originSelection is {} selected&&corridor.OriginSideOrders.Contains(selected.ExitOrder);
            bool mainEntry=corridor.StartKind==AuxiliaryBoundaryKind.IndependentEntrance
                ?route.Any(e=>NativeLaneGraph.Prefab(e)==NativeLaneGraph.Prefab(corridor.Origin)&&e.StartNodeUid==corridor.MainEntrance)
                :originSelection is {} kept&&!corridor.OriginSideOrders.Contains(kept.ExitOrder);
            // A mid-corridor initialization can use the selected native lane or the explicit initial planning transfer.
            if(!sideEntry&&!mainEntry)
            {
                var initial=lanes.InitialTransfer;
                var first=corridor.Sections.FirstOrDefault(s=>Find(s.Edge)>=0);
                if(first is not null)
                {
                    int? order=initial?.FromLane;
                    if(order is null&&NativeLaneGraph.Prefab(first.Edge) is not null)
                        order=lanes.Sections.FirstOrDefault(s=>s.EdgeId==first.Edge.Id)?.Path.EntryOrder;
                    sideEntry=order is {} n&&first.SideOrders.Contains(n);
                    mainEntry=order is {} m&&first.MainOrders.Contains(m);
                    if(order is null)
                    {
                        // No initial transfer means the chosen future native chain is already reachable in the current lane.
                        foreach(var selection in lanes.Sections.Where(s=>route.Any(e=>e.Id==s.EdgeId)))
                        {
                            var section=corridor.Sections.FirstOrDefault(s=>s.Edge.Id==selection.EdgeId);
                            var boundary=corridor.Terminals.FirstOrDefault(t=>t.Edge.Id==selection.EdgeId);
                            if(section is null&&boundary is null)continue;
                            var sideOrders=section?.SideOrders??boundary!.SideOrders;
                            var mainOrders=section?.MainOrders??boundary!.MainOrders;
                            sideEntry=sideOrders.Contains(selection.Path.EntryOrder);mainEntry=mainOrders.Contains(selection.Path.EntryOrder);break;
                        }
                    }
                }
            }
            var existing=result.FirstOrDefault(a=>a.JunctionId==junction&&a.Kind is ManeuverKind.ChooseLanes or ManeuverKind.KeepLeft or ManeuverKind.KeepRight);
            if(terminal.Terminal.SideOnly)
            {
                var signal=terminal.Terminal.Edge.ForkInfo?.Branches.FirstOrDefault(b=>b.ExitNode==terminal.Terminal.Edge.EndNodeUid)?.Signal;
                bool left=signal is ForkSignal.Left or ForkSignal.Right?signal==ForkSignal.Left:corridor.SideIsLeft.Value;
                var kind=left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight;
                var action=existing??new NavigationManeuver("auxiliary-exit:"+corridor.Id+":"+terminal.Terminal.Edge.Id,kind,end,"lane",terminal.Terminal.Edge.Id){JunctionId=junction,JunctionKind=NavigationJunctionKind.Ramp};
                result.Remove(action);
                result.Add(action with{Kind=kind,DistanceMeters=AuxiliaryLaneTiming.ActionPoint(interval),EndDistanceMeters=end,
                    ApproachInterval=interval,Instruction=null,NearInstruction=null,
                    ForkInstruction=action.ForkInstruction is {} f?f with{Kind=kind,Text=null}:null});
            }
            else if(sideEntry&&(terminal.Terminal.MainOnly||terminal.Terminal.Merges))
            {
                // Navigation requires the main exit: the independently added side lane either merges physically
                // or leads to a different road. The ordinary corridor permits the necessary planning transfer.
                bool left=terminal.Terminal.NativeMoveLeft??!corridor.SideIsLeft.Value;
                result.RemoveAll(a=>a.JunctionId==junction&&a.Kind is ManeuverKind.ChooseLanes or ManeuverKind.KeepLeft or ManeuverKind.KeepRight);
                result.Add(new("auxiliary-merge:"+corridor.Id+":"+terminal.Terminal.Edge.Id,left?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,
                    AuxiliaryLaneTiming.ActionPoint(interval),corridor.Origin.Id,terminal.Terminal.Edge.Id)
                {JunctionId=junction,JunctionKind=NavigationJunctionKind.Ramp,EndDistanceMeters=end,
                    HasSeparateApproachRoad=false,ApproachInterval=interval});
            }
            else if((mainEntry||sideEntry)&&existing is not null)
            {
                result.Remove(existing);result.Add(existing with{Kind=ManeuverKind.Straight,AnnouncementMode=NavigationAnnouncementMode.Silent,
                    IsMainRoadContinuation=true,Instruction=null,NearInstruction=null,
                    ForkInstruction=existing.ForkInstruction is {} f?f with{Kind=ManeuverKind.Straight,Silent=true,Text=null}:null});
            }
        }
        return result.DistinctBy(a=>a.Id).OrderBy(a=>a.DistanceMeters).ToArray();
        int Find(DirectedRoad edge)
        {
            var key=NativeRoadKey.Of(edge);for(int i=0;i<route.Count;i++)if(NativeRoadKey.Of(route[i])==key)return i;return -1;
        }
    }
}
