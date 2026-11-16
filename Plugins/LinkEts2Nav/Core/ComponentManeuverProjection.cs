using System.Numerics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Core;

public sealed record ComponentManeuverProjectionResult(IReadOnlyList<NavigationManeuver> Actions,
    IReadOnlyList<MapQueryIssue> Issues,IReadOnlyList<NavigationUnresolvedSpan> Unresolved);

/// <summary>Maps static component facts onto one concrete route visit. It never reclassifies priority or lane survival.</summary>
public static class ComponentManeuverProjection
{
    public static ComponentManeuverProjectionResult Build(IReadOnlyList<DirectedRoad> route,int visit,
        IReadOnlyList<ComponentPassageContinuity> entryFacts,IReadOnlyList<ComponentPassageContinuity> exitFacts)
    {
        var actions=new List<NavigationManeuver>();var issues=new List<MapQueryIssue>();var unresolved=new List<NavigationUnresolvedSpan>();
        var offsets=new double[route.Count+1];for(int i=0;i<route.Count;i++)offsets[i+1]=offsets[i]+RouteGeometry.Length(route[i]);
        var edge=route[visit];
        bool Selected(ComponentPassageContinuity fact)=>fact.EdgeId==edge.Id&&fact.EntryNodeUid==edge.StartNodeUid&&fact.ExitNodeUid==edge.EndNodeUid;
        foreach(var fact in entryFacts.Where(Selected))Add(fact,false);
        foreach(var fact in exitFacts.Where(Selected))Add(fact,true);
        return new(actions.OrderBy(a=>a.DistanceMeters).ToArray(),issues,unresolved);

        void Fail(ComponentPassageContinuity fact,string reason)
        {
            issues.Add(fact.Issue??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,reason)
                {QueryAnchor=edge.Points.FirstOrDefault()});
            unresolved.Add(new(offsets[visit],offsets[visit+1]));
        }
        void Add(ComponentPassageContinuity fact,bool outgoing)
        {
            if(fact.Kind is ComponentContinuityKind.Unknown or ComponentContinuityKind.Singular)
            {Fail(fact,"组件主侧关系或车道终点尚未确定");return;}
            bool choose=fact.Kind==ComponentContinuityKind.Natural||fact.IsMain&&outgoing&&fact.HasNaturalAlternative;
            if(fact.IsMain&&!choose)
            {
                if(outgoing||entryFacts.Count==0)return;
                actions.Add(new($"component:{fact.EdgeId}:through",ManeuverKind.Straight,offsets[visit],edge.Id,edge.Id)
                {JunctionId=$"prefab:{fact.PrefabUid:X16}",JunctionKind=NavigationJunctionKind.Ramp,
                    EndDistanceMeters=offsets[visit+1],IsMainRoadContinuation=true});
                return;
            }
            var position=Locate(fact.ActionPoint,outgoing);var end=Locate(fact.LifecycleEnd,outgoing);
            if(position is null||end is null||end<position-.001)
            {Fail(fact,"组件动作点或真实结束点不在本次导航访问中");return;}
            string? text;ManeuverKind kind;RoadForkInstruction? fork=null;
            if(choose)
            {
                var orders=outgoing?fact.EntryOrders:fact.ExitOrders;
                int count=outgoing?fact.EntryLaneCount:fact.ExitLaneCount;
                text=LaneInstructionFormatter.Selection(count,orders);
                if(text is null)
                {
                    // All lanes constitute continuation, not a fabricated instruction to use every lane.
                    if(orders.Distinct().Count()==count&&count>0)
                    {
                        actions.Add(new($"component:{fact.EdgeId}:through",ManeuverKind.Straight,offsets[visit],edge.Id,edge.Id)
                        {JunctionId=$"prefab:{fact.PrefabUid:X16}",JunctionKind=NavigationJunctionKind.Ramp,
                            EndDistanceMeters=end,IsMainRoadContinuation=true});
                    }
                    else Fail(fact,"自然组件的有效车道集合尚未确定");
                    return;
                }
                NavigationSide side=NavigationSide.None;
                if(outgoing)
                {
                    if(fact.LegalExitCount>=3)text="有多个相近出口，请"+text;
                    else if(fact.LegalExitCount==2)
                    {
                        var order=ExitSide(fact,exitFacts);
                        side=order;
                        text=order==NavigationSide.None?"分岔口，请"+text:
                            "分岔口靠"+(order==NavigationSide.Left?"左":"右")+"，"+text;
                    }
                }
                kind=ManeuverKind.ChooseLanes;
                fork=new(kind,false,text,orders,orders){Side=side};
            }
            else
            {
                if(fact.Turn is not {Side:NavigationSide.Left or NavigationSide.Right} turn||turn.Kind==ComponentTurnKind.Unknown)
                {Fail(fact,"组件转向侧无法判定，不能推测播报方向");return;}
                bool left=turn.Side==NavigationSide.Left;
                kind=fact.Kind==ComponentContinuityKind.Soft||turn.Kind==ComponentTurnKind.Forward
                    ?left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight:
                    turn.Kind==ComponentTurnKind.Back?left?ManeuverKind.RearLeft:ManeuverKind.RearRight:
                    left?ManeuverKind.TurnLeft:ManeuverKind.TurnRight;
                text=NavigationPrompts.Action(kind);
            }
            LaneActionInterval? interval=null;
            if(fact.Kind==ComponentContinuityKind.Soft)
            {
                var start=Locate(fact.IntervalStart,outgoing);var finish=Locate(fact.IntervalEnd,outgoing);
                if(start is null||finish is null||finish<start-.001)
                {Fail(fact,"软组件的真实动作区间不在本次导航访问中");return;}
                interval=new(start.Value,finish.Value);
            }
            string id=$"component:{fact.EdgeId}:{(outgoing?"exit":"entry")}:{fact.Kind}";
            string? execution=!outgoing&&fact.Kind==ComponentContinuityKind.Hard&&fact.Turn is {} approach
                ?text+"，注意"+(approach.Side==NavigationSide.Left?"右":"左")+"侧来车":null;
            actions.Add(new(id,kind,position.Value,edge.Id,edge.Id)
            {JunctionId=$"prefab:{fact.PrefabUid:X16}",JunctionKind=NavigationJunctionKind.Ramp,EndDistanceMeters=end,
                Instruction=text,ExecutionInstruction=execution,ForkInstruction=fork,ApproachInterval=interval,
                HasSeparateApproachRoad=visit>0&&NativeLaneGraph.Prefab(route[visit-1]) is null});
        }
        double? Locate(ComponentBoundary? boundary,bool outgoing)
        {
            if(boundary is null||!double.IsFinite(boundary.OffsetMeters))return null;
            // An entrance looks downstream; an exit's approach may lie upstream.
            // Bind the current visit before searching in that physical direction.
            var indices=outgoing?Enumerable.Range(0,visit+1).Reverse().Concat(Enumerable.Range(visit+1,route.Count-visit-1))
                :Enumerable.Range(visit,route.Count-visit).Concat(Enumerable.Range(0,visit).Reverse());
            foreach(int i in indices)
                if(route[i].Id==boundary.EdgeId)
                {
                    double length=RouteGeometry.Length(route[i]);
                    if(boundary.OffsetMeters<-.001||boundary.OffsetMeters>length+.001)return null;
                    return offsets[i]+Math.Clamp(boundary.OffsetMeters,0,length);
                }
            return null;
        }
    }
    private static NavigationSide ExitSide(ComponentPassageContinuity selected,IReadOnlyList<ComponentPassageContinuity> exits)
    {
        var other=exits.Where(f=>f.ExitNodeUid!=selected.ExitNodeUid).DistinctBy(f=>f.ExitNodeUid).ToArray();
        if(other.Length!=1)return NavigationSide.None;
        if(selected.ExitTurnRadians is {} a&&other[0].ExitTurnRadians is {} b&&double.IsFinite(a)&&double.IsFinite(b)&&a!=b)
            return a<b?NavigationSide.Left:NavigationSide.Right;
        if(selected.EntryOrders.Count>0&&other[0].EntryOrders.Count>0)
        {
            if(selected.EntryOrders.Max()<other[0].EntryOrders.Min())return NavigationSide.Left;
            if(selected.EntryOrders.Min()>other[0].EntryOrders.Max())return NavigationSide.Right;
        }
        return NavigationSide.None;
    }
}
