using System.Numerics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Core;

/// <summary>Snapshot-local component roles. Construction owns all traversal memo; queries do not inspect a player or route.</summary>
public sealed class ComponentContinuityIndex
{
    private readonly IReadOnlyDictionary<string,ComponentPassageContinuity> entries,exits;
    public IReadOnlyList<MapQueryIssue> Issues {get;}
    private ComponentContinuityIndex(Dictionary<string,ComponentPassageContinuity> entries,
        Dictionary<string,ComponentPassageContinuity> exits,IReadOnlyList<MapQueryIssue> issues)
    {this.entries=entries;this.exits=exits;Issues=issues;}
    public ComponentPassageContinuity? ForEntry(string edgeId)=>entries.GetValueOrDefault(edgeId);
    public ComponentPassageContinuity? ForExit(string edgeId)=>exits.GetValueOrDefault(edgeId);
    public IReadOnlyList<ComponentPassageContinuity> Entries=>entries.Values.ToArray();
    public IReadOnlyList<ComponentPassageContinuity> Exits=>exits.Values.ToArray();

    public static ComponentContinuityIndex Build(NativeLaneGraph graph,Func<ulong,MapQueryIssue>? missingConnection=null,
        Action<string>? log=null,CancellationToken cancellationToken=default)
    {
        var forward=new Builder(graph.ThroughTraffic,missingConnection,false,cancellationToken);
        var entries=forward.Build();
        var reverseGeometry=new Dictionary<NativeLaneGeometry,NativeLaneGeometry>(ReferenceEqualityComparer.Instance);
        var originals=graph.Edges.GroupBy(NativeRoadKey.Of).ToDictionary(g=>g.Key,g=>g.First());
        var originalContinuity=forward.LocalContinuity;
        var outletCounts=graph.Edges.Where(e=>e.LanePaths.Count>0).GroupBy(e=>(e.LanePaths[0].PrefabUid,e.StartNodeUid))
            .ToDictionary(g=>g.Key,g=>g.Select(e=>e.EndNodeUid).Distinct().Count());
        var backward=new Builder(new NativeLaneGraph(graph.ThroughTraffic.Edges.Select(e=>Reverse(e,reverseGeometry)).ToArray()),missingConnection,true,cancellationToken);
        var exits=backward.Build().ToDictionary(p=>p.Key,p=>Restore(p.Value,originals,originalContinuity,outletCounts));
        foreach(var fact in exits.Values.Where(f=>f.Kind==ComponentContinuityKind.Natural))
            foreach(var main in exits.Values.Where(f=>f.IsMain&&f.EntryNodeUid==fact.EntryNodeUid&&f.PrefabUid==fact.PrefabUid).ToArray())
                exits[main.EdgeId]=main with{HasNaturalAlternative=true};
        var issues=forward.Issues.Concat(backward.Issues).Distinct().ToArray();
        foreach(var issue in issues)log?.Invoke(issue.Reason);
        return new(entries,exits,issues);
    }

    private static DirectedRoad Reverse(DirectedRoad edge,Dictionary<NativeLaneGeometry,NativeLaneGeometry> geometry)
    {
        NativeLaneGeometry? Reversed(NativeLaneGeometry? original)
        {
            if(original is null)return null;
            if(!geometry.TryGetValue(original,out var value))geometry[original]=value=new(original.PrefabUid,
                // This reverse merge represents an actual split, which deliberately ignores lamps.
                original.Curves.Select(c=>new NativeLaneCurve(c.Index,c.End,c.Start,-c.EndTangent,-c.StartTangent,c.Previous,c.Next)
                    {MapIssue=c.MapIssue,Blinker=LaneBlinker.NoBlinker}).ToArray())
                {InputCurveIndices=original.OutputCurveIndices,OutputCurveIndices=original.InputCurveIndices};
            return value;
        }
        return edge with
        {
            StartNodeUid=edge.EndNodeUid,EndNodeUid=edge.StartNodeUid,Points=edge.Points.Reverse().ToArray(),
            NativeEntryTangent=-edge.NativeExitTangent,NativeExitTangent=-edge.NativeEntryTangent,
            JoinPriority=edge.SplitPriority,SplitPriority=edge.JoinPriority,
            LanePaths=edge.LanePaths.Select(p=>
            {
                int n=p.Points.Count;var starts=p.CurveStartPointIndices;
                var reversedStarts=starts.Count==p.CurveIndices.Count
                    ?Enumerable.Range(0,starts.Count).Select(i=>i==0?0:n-(starts[starts.Count-i])).ToArray():[];
                return new PrefabLanePath(p.PrefabUid,p.ExitNodeUid,p.EntryNodeUid,p.ExitLane,p.EntryLane,p.ExitOrder,p.EntryOrder,
                    p.ExitLaneCount,p.EntryLaneCount,p.CurveIndices.Reverse().ToArray(),p.Points.Reverse().ToArray(),p.LengthMeters)
                    {NativeGeometry=Reversed(p.NativeGeometry),CurveStartPointIndices=reversedStarts};
            }).ToArray()
        };
    }

    private static ComponentPassageContinuity Restore(ComponentPassageContinuity fact,IReadOnlyDictionary<NativeRoadKey,DirectedRoad> originals,
        IReadOnlyDictionary<ulong,LaneContinuityResult> continuity,IReadOnlyDictionary<(ulong,ulong),int> outletCounts)
    {
        var edge=originals[new(fact.EdgeId,fact.ExitNodeUid,fact.EntryNodeUid)];
        ComponentBoundary? Point(ComponentBoundary? point)
        {
            if(point is null)return null;
            var original=originals[new(point.EdgeId,point.EndNodeUid,point.StartNodeUid)];
            return point with{OffsetMeters=Math.Max(0,RouteGeometry.Length(original)-point.OffsetMeters),
                Interface=point.Interface switch{ComponentBoundaryInterface.Start=>ComponentBoundaryInterface.End,
                    ComponentBoundaryInterface.End=>ComponentBoundaryInterface.Start,_=>ComponentBoundaryInterface.None},
                StartNodeUid=original.StartNodeUid,EndNodeUid=original.EndNodeUid};
        }
        // A reverse search discovers the beginning of a deceleration lane. Its
        // spoken action is nevertheless measured in the actual travel direction.
        var start=Point(fact.IntervalEnd);var end=Point(fact.IntervalStart);
        ComponentBoundary? action=Point(fact.ActionPoint);
        var exit=new ComponentBoundary(edge.Id,RouteGeometry.Length(edge),edge.Points[^1]){StartNodeUid=edge.StartNodeUid,EndNodeUid=edge.EndNodeUid,Interface=ComponentBoundaryInterface.End};
        ComponentTurn? ActualTurn(ComponentLaneOutcome lane)
        {
            if(lane.TurnPrefabUid is {} uid&&lane.TurnSharedCurveIndex is {} shared&&lane.TurnBranchCurveIndex is {} branch)
            {
                var native=continuity.GetValueOrDefault(uid)?.Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==shared)
                    ?.Branches.FirstOrDefault(b=>b.CurveIndex==branch);
                return native is null?new(NavigationSide.None,ComponentTurnKind.Unknown,"实际分岔缺少原生转向结果"):ConvertTurn(native.Turn);
            }
            // A reverse separation is a forward joining of parallel lane groups.
            // Its physical side is unchanged by reversing both travel and transfer.
            return lane.Turn;
        }
        bool Same(ComponentBoundary? a,ComponentBoundary? b)=>a is not null&&b is not null&&a.EdgeId==b.EdgeId
            &&a.StartNodeUid==b.StartNodeUid&&a.EndNodeUid==b.EndNodeUid&&Math.Abs(a.OffsetMeters-b.OffsetMeters)<1e-6;
        // Aggregate chooses its action from exactly one of these original
        // lane anchors. Restore that same owner, never an arbitrary first lane.
        ComponentBoundary? OwnerPoint(ComponentLaneOutcome lane)=>fact.Kind switch
        {
            ComponentContinuityKind.Main or ComponentContinuityKind.Natural=>lane.InitialMidpoint,
            ComponentContinuityKind.Hard=>lane.Terminal,
            ComponentContinuityKind.Soft=>lane.InComponent&&lane.Fate==ComponentLaneFate.Dies?lane.Terminal:lane.SoftAction,
            _=>null
        };
        var owner=fact.Kind is ComponentContinuityKind.Unknown or ComponentContinuityKind.Singular?null:
            fact.Lanes.FirstOrDefault(l=>Same(OwnerPoint(l),fact.ActionPoint))
                ??throw new ArgumentException($"Reverse component {fact.EdgeId} ({fact.Kind}) has no lane owning its action anchor.",nameof(fact));
        var turns=fact.Lanes.Select(ActualTurn).ToArray();
        var directions=turns.Where(t=>t?.Side is NavigationSide.Left or NavigationSide.Right).Select(t=>t!.Side).Distinct().ToArray();
        var issue=fact.Issue;
        if(issue?.Reason=="同一组件入口的车道动作方向冲突")issue=fact.Lanes.Select(l=>l.Issue).OfType<MapQueryIssue>().FirstOrDefault();
        if(directions.Length>1)issue=new MapQueryFailure(MapQueryFailureKind.Ambiguous,"同一组件出口的车道动作方向冲突");
        return fact with
        {
            EntryNodeUid=edge.StartNodeUid,ExitNodeUid=edge.EndNodeUid,
            EntryOrders=fact.ExitOrders,ExitOrders=fact.EntryOrders,
            EntryLaneCount=fact.ExitLaneCount,ExitLaneCount=fact.EntryLaneCount,
            LegalExitCount=outletCounts.GetValueOrDefault((fact.PrefabUid,edge.StartNodeUid)),
            ActionPoint=action,LifecycleEnd=exit,IntervalStart=start,IntervalEnd=end,
            Turn=directions.Length>1?null:owner is null?null:ActualTurn(owner),Issue=issue,
            Lanes=fact.Lanes.Select((l,i)=>l with
            {
                EntryOrder=l.ExitOrder,ExitOrder=l.EntryOrder,
                Fate=l.Fate==ComponentLaneFate.Dies?ComponentLaneFate.Born:l.Fate==ComponentLaneFate.Separates?ComponentLaneFate.Joins:l.Fate,
                InitialMidpoint=Point(l.InitialMidpoint)!,InitialExit=exit,Terminal=Point(l.Terminal),LifecycleEnd=exit,
                SoftAction=Point(l.SoftAction),Turn=turns[i]
            }).ToArray()
        };
    }
    private static ComponentTurn ConvertTurn(LaneTurnKind turn)=>turn switch
    {
        LaneTurnKind.ForwardLeft=>new(NavigationSide.Left,ComponentTurnKind.Forward),
        LaneTurnKind.ForwardRight=>new(NavigationSide.Right,ComponentTurnKind.Forward),
        LaneTurnKind.Left=>new(NavigationSide.Left,ComponentTurnKind.Turn),
        LaneTurnKind.Right=>new(NavigationSide.Right,ComponentTurnKind.Turn),
        LaneTurnKind.RearLeft=>new(NavigationSide.Left,ComponentTurnKind.Back),
        LaneTurnKind.RearRight=>new(NavigationSide.Right,ComponentTurnKind.Back),
        _=>new(NavigationSide.None,ComponentTurnKind.Unknown,"原生切线无法确定转向方向")
    };

    private sealed class Builder
    {
        private readonly NativeLaneGraph graph;
        private readonly Func<ulong,MapQueryIssue>? missing;
        private readonly bool reverse;
        private readonly CancellationToken token;
        private readonly Dictionary<ulong,LaneContinuityResult> continuity=[];
        public IReadOnlyDictionary<ulong,LaneContinuityResult> LocalContinuity=>continuity;
        private readonly Dictionary<ulong,IReadOnlyList<DirectedRoad>> prefabs;
        private readonly Dictionary<ulong,IReadOnlyList<DirectedRoad>> starts;
        public List<MapQueryIssue> Issues {get;}=[];
        private sealed record Segment(DirectedRoad Edge,PrefabLanePath? Lane,double Start,double Length);
        private sealed record Local(DirectedRoad Edge,PrefabLanePath Lane,LaneContinuityBranch? Death,
            Vector3? DeathPoint,bool IdentityPreserved,MapQueryIssue? Issue);

        public Builder(NativeLaneGraph graph,Func<ulong,MapQueryIssue>? missing,bool reverse,CancellationToken token)
        {
            this.graph=graph;this.missing=missing;this.reverse=reverse;this.token=token;
            prefabs=graph.Edges.SelectMany(e=>e.LanePaths.Select(p=>(p.PrefabUid,Edge:e))).GroupBy(p=>p.PrefabUid)
                .ToDictionary(g=>g.Key,g=>(IReadOnlyList<DirectedRoad>)g.Select(p=>p.Edge).DistinctBy(e=>e.Id).ToArray());
            starts=graph.Edges.GroupBy(e=>e.StartNodeUid).ToDictionary(g=>g.Key,g=>(IReadOnlyList<DirectedRoad>)g.ToArray());
            foreach(var family in graph.Edges.SelectMany(e=>e.LanePaths).Where(p=>p.NativeGeometry is not null).GroupBy(p=>p.PrefabUid))
                continuity[family.Key]=LaneContinuity.Analyze(family.First().NativeGeometry!);
        }

        public Dictionary<string,ComponentPassageContinuity> Build()
        {
            var result=new Dictionary<string,ComponentPassageContinuity>(StringComparer.Ordinal);
            foreach(var group in graph.Edges.Where(e=>e.LanePaths.Count>0&&e.Junction?.IsJunction!=true&&e.Roundabout is null)
                .GroupBy(e=>(e.LanePaths[0].PrefabUid,e.EndNodeUid)))
            {
                token.ThrowIfCancellationRequested();
                var inputs=group.Select(e=>e.StartNodeUid).Distinct().ToArray();
                if(inputs.Length<2)continue;
                var priority=group.Select(e=>e.JoinPriority).OfType<RoadJoinPriority>().FirstOrDefault();
                if(priority?.MainEntry is not {} main)
                {
                    foreach(var edge in group)result[edge.Id]=Unknown(edge,priority,priority?.Reason??"组件缺少完整主入口评分");
                    continue;
                }
                var all=prefabs[group.Key.PrefabUid];
                var mainPaths=all.Where(e=>e.StartNodeUid==main).SelectMany(e=>e.LanePaths.Select(p=>Inspect(e,p)))
                    .Where(p=>p.IdentityPreserved&&p.Death is null&&p.Issue is null).ToArray();
                foreach(var family in group.GroupBy(e=>e.StartNodeUid))
                {
                    var first=family.First();
                    int exits=all.Where(e=>e.StartNodeUid==family.Key).Select(e=>e.EndNodeUid).Distinct().Count();
                    var rootLanes=all.Where(e=>e.StartNodeUid==family.Key).SelectMany(e=>e.LanePaths)
                        .Select(p=>p.EntryOrder).Distinct().Order().ToArray();
                    var outcomes=rootLanes.Select(order=>Walk(first,order,mainPaths,family.Key==main)).ToArray();
                    foreach(var edge in family)
                    {
                        var fact=ComponentContinuity.Aggregate(edge,family.Key==main,outcomes,exits,reverse);
                        if(fact.Issue is {} issue)Issues.Add(issue);
                        result[edge.Id]=fact;
                    }
                }
            }
            return result;
        }
        private ComponentPassageContinuity Unknown(DirectedRoad edge,RoadJoinPriority? priority,string reason)
        {
            var missingObjects=(priority?.MapIssues??[]).OfType<MissingMapObjects>().SelectMany(i=>i.Keys).Distinct().ToArray();
            MapQueryIssue issue=missingObjects.Length>0?new MissingMapObjects(missingObjects,reason)
                :priority?.MapIssues.FirstOrDefault()??new MapQueryFailure(MapQueryFailureKind.Ambiguous,reason);
            issue=issue with{QueryAnchor=edge.Points.FirstOrDefault()};Issues.Add(issue);
            return new(edge.Id,edge.LanePaths[0].PrefabUid,edge.StartNodeUid,edge.EndNodeUid,false,ComponentContinuityKind.Unknown,
                null,null,null,null,edge.LanePaths.Select(p=>p.EntryOrder).Distinct().Order().ToArray(),
                edge.LanePaths.Select(p=>p.ExitOrder).Distinct().Order().ToArray(),edge.LanePaths.Max(p=>p.EntryLaneCount),
                edge.LanePaths.Max(p=>p.ExitLaneCount),0,null,[],issue);
        }

        private Local Inspect(DirectedRoad edge,PrefabLanePath path)
        {
            if(!continuity.TryGetValue(path.PrefabUid,out var facts))
                return new(edge,path,null,null,false,new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,"组件缺少合法原生曲线几何"));
            if(facts.MapIssue is {} issue)return new(edge,path,null,null,false,issue);
            for(int i=0;i<path.CurveIndices.Count;i++)
            {
                int curve=path.CurveIndices[i];
                if(i>0)
                {
                    var merge=facts.Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==curve);
                    var branch=merge?.Branches.FirstOrDefault(b=>b.CurveIndex==path.CurveIndices[i-1]);
                    if(branch?.Role==LaneContinuityRole.Unknown)return new(edge,path,null,null,false,branch.MapIssue??merge?.MapIssue);
                    if(branch?.Role==LaneContinuityRole.Dead)return new(edge,path,branch,merge!.Origin,true,null);
                }
                if(i+1<path.CurveIndices.Count)
                {
                    var split=facts.Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==curve);
                    var branch=split?.Branches.FirstOrDefault(b=>b.CurveIndex==path.CurveIndices[i+1]);
                    if(branch is {Role:LaneContinuityRole.Unknown})return new(edge,path,null,null,false,branch.MapIssue??split?.MapIssue);
                    if(branch is {Role:LaneContinuityRole.Newborn})return new(edge,path,null,null,false,null);
                }
            }
            return new(edge,path,null,null,true,null);
        }

        private ComponentLaneOutcome Walk(DirectedRoad initial,int order,Local[] mains,bool mainEntrance)
        {
            var family=prefabs[initial.LanePaths[0].PrefabUid].Where(e=>e.StartNodeUid==initial.StartNodeUid)
                .SelectMany(e=>e.LanePaths.Where(p=>p.EntryOrder==order).Select(p=>Inspect(e,p))).ToArray();
            var chosen=Choose(family);
            var path=chosen?.Lane??family.First().Lane;
            var originEdge=chosen?.Edge??initial;
            double initialLength=Length(path);var trace=new List<Segment>{new(originEdge,path,-initialLength,initialLength)};
            var midpoint=At(originEdge,path,initialLength/2);var exit=At(originEdge,path,initialLength);
            ComponentLaneOutcome Result(ComponentLaneFate fate,bool current,double distance,ComponentBoundary? terminal=null,
                ComponentBoundary? lifecycle=null,ComponentTurn? turn=null,MapQueryIssue? issue=null)
            {
                double actionDistance=Math.Max(0,distance-ComponentContinuity.AdvanceMeters);
                if(reverse&&fate is ComponentLaneFate.Dies or ComponentLaneFate.Separates)
                    actionDistance=Math.Min(Math.Max(0,distance),ComponentContinuity.AdvanceMeters);
                return new(order,path.ExitOrder,fate,current,distance,midpoint,exit,terminal,lifecycle,
                    At(trace,actionDistance),turn,issue)
                {
                    MidpointDistanceFromExitMeters=-initialLength/2,
                    LifecycleDistanceFromExitMeters=current?0:distance+(lifecycle is not null&&terminal is not null&&lifecycle.EdgeId==terminal.EdgeId?lifecycle.OffsetMeters-terminal.OffsetMeters:0),
                    SoftActionDistanceFromExitMeters=actionDistance,
                    StructuralConstraintId=fate==ComponentLaneFate.Dies&&chosen?.Death is {} death
                        ?Constraint(chosen,death):fate==ComponentLaneFate.Separates&&terminal is not null
                            ?$"{(reverse?"component-joining":"component-separation")}:{chosen!.Lane.PrefabUid:X16}:{chosen.Edge.StartNodeUid:X16}:{chosen.Edge.EndNodeUid:X16}":null,
                    TurnPrefabUid=fate==ComponentLaneFate.Dies?chosen!.Lane.PrefabUid:null,
                    TurnSharedCurveIndex=fate==ComponentLaneFate.Dies?DeathPoint(chosen!,chosen!.Death!).SharedCurveIndex:null,
                    TurnBranchCurveIndex=fate==ComponentLaneFate.Dies?chosen!.Death!.CurveIndex:null
                };
            }
            ComponentLaneOutcome Missing(string reason,ulong node,MapQueryIssue? issue=null)
                =>Result(ComponentLaneFate.Unresolved,false,0,issue:(issue??missing?.Invoke(node)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,reason)) with{QueryAnchor=initial.Points[0]});
            if(chosen is null)return Missing("原生车道没有唯一身份延续",initial.StartNodeUid,
                family.Select(p=>p.Issue).OfType<MapQueryIssue>().FirstOrDefault()??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"原生车道没有唯一身份延续"));
            // The scored main entrance needs only its local identity proof.
            // Its downstream graph is a dependency of side-lane classification,
            // not of the already confirmed main continuation in this component.
            if(mainEntrance&&chosen.Death is null)
                return Result(ComponentLaneFate.Continues,true,0);
            double distance=-initialLength;bool current=true;
            // A repeated edge is another measured occurrence. Positive lengths
            // make the finite-graph traversal advance to its distance budget.
            while(true)
            {
                token.ThrowIfCancellationRequested();
                if(mains.Length==0&&chosen.Death is null)return Missing("主线资格已丢失，无法确认最近主线",chosen.Edge.StartNodeUid,
                    new MapQueryFailure(MapQueryFailureKind.Ambiguous,"主线资格已丢失，无法确认最近主线"));
                var nearest=mains.Select(m=>(Main:m,Distance:Vector3.DistanceSquared(chosen.Lane.Points[0],m.Lane.Points[0])))
                    .OrderBy(p=>p.Distance).ToArray();
                if(nearest.Length>1&&Math.Abs(nearest[0].Distance-nearest[1].Distance)<1e-6
                    &&nearest[0].Main.Lane.ExitNodeUid!=nearest[1].Main.Lane.ExitNodeUid)
                    return Missing("最近主线不唯一",chosen.Edge.StartNodeUid,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"最近主线不唯一"));
                if(nearest.Length>0&&chosen.Lane.ExitNodeUid!=nearest[0].Main.Lane.ExitNodeUid)
                {
                    var terminal=At(chosen.Edge,chosen.Lane,0);
                    var (direction,directionIssue)=Toward(chosen.Lane,nearest[0].Main.Lane);
                    return Result(ComponentLaneFate.Separates,current,distance,terminal,terminal,direction,
                        directionIssue);
                }
                if(chosen.Death is {} dead)
                {
                    double local=new LanePolyline(chosen.Lane.Points).Project(chosen.DeathPoint!.Value).Along;
                    double at=distance+local;
                    if(at>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,at);
                    return Result(ComponentLaneFate.Dies,current,at,At(chosen.Edge,chosen.Lane,local),
                        At(chosen.Edge,chosen.Lane,Length(chosen.Lane)),ConvertTurn(dead.Turn),dead.MapIssue);
                }
                distance=LaneTraceDistance.Advance(distance,Length(chosen.Lane));
                if(distance>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,distance);
                ulong node=chosen.Edge.EndNodeUid;int lane=chosen.Lane.ExitOrder;
                var mainOrders=mains.Where(m=>m.Lane.ExitNodeUid==node).Select(m=>m.Lane.ExitOrder).ToHashSet();
                string previousPrefab=NativeLaneGraph.Prefab(chosen.Edge)!;string? previousRoad=null;
                while(true)
                {
                    var next=(starts.GetValueOrDefault(node)??[]).Where(e=>NativeLaneGraph.Prefab(e)!=previousPrefab&&e.Id!=previousRoad).ToArray();
                    var roads=next.Where(e=>NativeLaneGraph.Prefab(e) is null).ToArray();
                    if(roads.Length==0)break;
                    if(roads.Length!=1||next.Length!=1)return Missing("相接普通道路不唯一",node,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接普通道路不唯一"));
                    var road=roads[0];double length=LaneTraceDistance.Positive(RouteGeometry.Length(road),$"Road {road.Id}");
                    trace.Add(new(road,null,distance,length));distance=LaneTraceDistance.Advance(distance,length);node=road.EndNodeUid;previousRoad=road.Id;
                    if(distance>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,distance);
                }
                var component=(starts.GetValueOrDefault(node)??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null&&NativeLaneGraph.Prefab(e)!=previousPrefab).ToArray();
                if(component.Length==0)return Missing("500米窗口内相接组件尚未加载",node);
                if(component.Any(e=>e.Junction?.IsJunction==true||e.Roundabout is not null))
                    return Result(ComponentLaneFate.Continues,false,distance);
                if(component.Select(e=>NativeLaneGraph.Prefab(e)).Distinct().Count()!=1)
                    return Missing("相接预制件不唯一",node,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接预制件不唯一"));
                var options=component.SelectMany(e=>e.LanePaths.Where(p=>p.EntryOrder==lane).Select(p=>Inspect(e,p))).ToArray();
                chosen=Choose(options);
                if(chosen is null)return Missing("相接组件车道没有唯一原生延续",node,
                    options.Select(p=>p.Issue).OfType<MapQueryIssue>().FirstOrDefault()??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接组件车道没有唯一原生延续"));
                mains=component.SelectMany(e=>e.LanePaths.Where(p=>mainOrders.Contains(p.EntryOrder)).Select(p=>Inspect(e,p)))
                    .Where(p=>p.IdentityPreserved&&p.Death is null&&p.Issue is null).ToArray();
                trace.Add(new(chosen.Edge,chosen.Lane,distance,Length(chosen.Lane)));current=false;
            }
        }
        private static Local? Choose(IEnumerable<Local> paths)
        {
            var options=paths.Where(p=>p.IdentityPreserved&&p.Issue is null)
                .DistinctBy(p=>(p.Edge.Id,p.Lane.EntryOrder,p.Lane.ExitOrder,string.Join(',',p.Lane.CurveIndices))).ToArray();
            if(options.Length==0)return null;
            // A legal native lane has one identity-preserving successor at each
            // local split. Distinct surviving chains are conflicting evidence,
            // not permission to select whichever sampled path is shortest.
            return options.Length==1?options[0]:null;
        }
        private static double Length(PrefabLanePath path)=>LaneTraceDistance.Positive(new LanePolyline(path.Points).Length,$"Native lane {path.PrefabUid:X16}:{path.EntryOrder}->{path.ExitOrder}");
        private static ComponentBoundary At(DirectedRoad edge,PrefabLanePath? lane,double distance)
        {
            var line=new LanePolyline(lane?.Points??edge.Points);var point=line.At(distance);
            var endpoint=distance==0?ComponentBoundaryInterface.Start:distance==line.Length?ComponentBoundaryInterface.End:ComponentBoundaryInterface.None;
            double offset=endpoint==ComponentBoundaryInterface.Start?0:endpoint==ComponentBoundaryInterface.End?RouteGeometry.Length(edge):new LanePolyline(edge.Points).Project(point).Along;
            return new(edge.Id,offset,point){StartNodeUid=edge.StartNodeUid,EndNodeUid=edge.EndNodeUid,Interface=endpoint};
        }
        private static ComponentBoundary At(IReadOnlyList<Segment> segments,double distance)
        {
            var segment=segments.FirstOrDefault(s=>distance<=s.Start+s.Length+.0001)??segments[^1];
            return At(segment.Edge,segment.Lane,Math.Clamp(distance-segment.Start,0,segment.Length));
        }
        private static (ComponentTurn Turn,MapQueryIssue? Issue) Toward(PrefabLanePath side,PrefabLanePath main)
        {
            var curve=side.NativeGeometry?.Curves.FirstOrDefault(c=>c.Index==side.CurveIndices[0]);
            var standard=main.NativeGeometry?.Curves.FirstOrDefault(c=>c.Index==main.CurveIndices[0]);
            if(curve is null||standard is null)return (new(NavigationSide.None,ComponentTurnKind.Unknown,"侧向并入缺少原生入口曲线"),
                new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,"侧向并入缺少原生入口曲线"));
            var description=LaneContinuity.DescribeTurn(side.NativeGeometry!,curve.Index,standard.StartTangent,standard.Start,true);
            if(description.MapIssue is {} issue)return (new(NavigationSide.None,ComponentTurnKind.Unknown,issue.Reason),issue);
            var turn=ConvertTurn(description.Turn);
            return (turn.Kind==ComponentTurnKind.Unknown?turn:turn with{Kind=ComponentTurnKind.Forward},null);
        }
        private string Constraint(Local local,LaneContinuityBranch branch)
        {
            var point=DeathPoint(local,branch);
            return $"{(reverse?"lane-split":"lane-merge")}:{local.Lane.PrefabUid:X16}:{point.SharedCurveIndex}";
        }
        private LaneContinuityPoint DeathPoint(Local local,LaneContinuityBranch branch)
            =>continuity[local.Lane.PrefabUid].Points.First(p=>p.Kind==LaneContinuityPointKind.Merge
                &&p.Branches.Any(b=>b.CurveIndex==branch.CurveIndex)&&p.Origin==local.DeathPoint);
    }
}
