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
        var reverseEdges=graph.ThroughTraffic.Edges.Select(e=>Reverse(e,reverseGeometry)).ToArray();
        var backward=new Builder(new NativeLaneGraph(reverseEdges),missingConnection,true,cancellationToken);
        var exits=forward.BuildExits(backward);
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

    private static ComponentBoundary? Restore(ComponentBoundary? point,
        IReadOnlyDictionary<NativeRoadKey,DirectedRoad> originals)
    {
        if(point is null)return null;
        var edge=originals[new(point.EdgeId,point.EndNodeUid,point.StartNodeUid)];
        return point with{OffsetMeters=Math.Max(0,RouteGeometry.Length(edge)-point.OffsetMeters),
            Interface=point.Interface switch{ComponentBoundaryInterface.Start=>ComponentBoundaryInterface.End,
                ComponentBoundaryInterface.End=>ComponentBoundaryInterface.Start,_=>ComponentBoundaryInterface.None},
            StartNodeUid=edge.StartNodeUid,EndNodeUid=edge.EndNodeUid};
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
        private readonly Dictionary<ulong,IReadOnlyList<DirectedRoad>> prefabs;
        private readonly Dictionary<ulong,IReadOnlyList<DirectedRoad>> starts;
        public List<MapQueryIssue> Issues {get;}=[];
        private sealed record Segment(DirectedRoad Edge,PrefabLanePath? Lane,double Start,double Length);
        private sealed record Local(DirectedRoad Edge,PrefabLanePath Lane,LaneContinuityBranch? Death,
            LaneContinuityPoint? Merge,bool IdentityPreserved,MapQueryIssue? Issue)
        {
            public int? NoMainSplitIndex {get;init;}
        }
        // Each comparable lane keeps its own measured progress. Different lane
        // arcs need not have the side lane's length between common interfaces.
        private sealed record Reference(Local Local,double StartDistance);

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
            foreach(var group in graph.Edges.Where(e=>e.LanePaths.Count>0&&e.Junction?.IsJunction!=true)
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
                        var fact=ComponentContinuity.Aggregate(edge,family.Key==main,outcomes,exits);
                        if(fact.Issue is {} issue)Issues.Add(issue);
                        result[edge.Id]=fact;
                    }
                }
            }
            return result;
        }
        public Dictionary<string,ComponentPassageContinuity> BuildExits(Builder backward)
        {
            var result=new Dictionary<string,ComponentPassageContinuity>(StringComparer.Ordinal);
            var originals=graph.Edges.GroupBy(NativeRoadKey.Of).ToDictionary(g=>g.Key,g=>g.First());
            var reversed=backward.graph.Edges.GroupBy(NativeRoadKey.Of).ToDictionary(g=>g.Key,g=>g.First());
            foreach(var group in graph.Edges.Where(e=>e.LanePaths.Count>0&&e.Junction?.IsJunction!=true)
                .GroupBy(e=>(e.LanePaths[0].PrefabUid,e.StartNodeUid)))
            {
                token.ThrowIfCancellationRequested();
                int exitCount=group.Select(e=>e.EndNodeUid).Distinct().Count();
                if(exitCount<2)continue;
                var priority=group.Select(e=>e.SplitPriority).OfType<RoadJoinPriority>().FirstOrDefault();
                var main=priority?.MainEntry;
                // Only complete scoring supplies a margin when there is no winner.
                if(main is null&&priority?.WinningMargin is null)
                {
                    foreach(var edge in group)result[edge.Id]=Unknown(edge,priority,priority?.Reason??"Missing outlet priority");
                    continue;
                }

                var localPaths=group.SelectMany(edge=>edge.LanePaths.Select(path=>(Edge:edge,Path:path,Split:FirstSideSplit(path)))).ToArray();
                var mainPassage=main is {} mainOutlet?group.Single(e=>e.EndNodeUid==mainOutlet):null;
                var origins=new Dictionary<int,ComponentLaneOutcome>();
                if(mainPassage is not null)
                {
                    var referenceOrders=localPaths.Where(p=>p.Edge.EndNodeUid==mainPassage.EndNodeUid&&p.Split is null)
                        .Select(p=>p.Path.EntryOrder).Distinct().ToArray();
                    foreach(var family in localPaths.GroupBy(p=>p.Path.EntryOrder))
                    {
                        // The selected path only provides an anchor at the common approach port.
                        // Upstream tracing starts at that port and never traverses this passage.
                        if(referenceOrders.Contains(family.Key)||family.All(p=>p.Split is not null))continue;
                        var root=family.First();
                        var reverseEdge=reversed[new(root.Edge.Id,root.Edge.EndNodeUid,root.Edge.StartNodeUid)];
                        var reversePath=reverseEdge.LanePaths.First(p=>p.ExitOrder==family.Key);
                        origins.Add(family.Key,backward.Trace(reverseEdge,reversePath,null,[],false,referenceOrders));
                    }
                }
                foreach(var paths in localPaths.GroupBy(p=>p.Edge.Id))
                {
                    var edge=paths.First().Edge;
                    var lanes=paths.Select(p=>OutletLane(edge,p.Path,p.Split,origins.GetValueOrDefault(p.Path.EntryOrder),mainPassage,originals)).ToArray();
                    var fact=ComponentContinuity.AggregateOutlet(edge,edge.EndNodeUid==main,lanes,exitCount);
                    if(fact.Issue is {} issue)Issues.Add(issue);
                    result[edge.Id]=fact;
                }
            }
            return result;
        }

        private (int Index,LaneContinuityPoint Point,LaneContinuityBranch Branch)? FirstSideSplit(PrefabLanePath path)
        {
            if(!continuity.TryGetValue(path.PrefabUid,out var facts))return null;
            for(int i=1;i<path.CurveIndices.Count;i++)
            {
                var split=facts.Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==path.CurveIndices[i-1]);
                var branch=split?.Branches.FirstOrDefault(b=>b.CurveIndex==path.CurveIndices[i]);
                if(branch is {Role:LaneContinuityRole.Newborn or LaneContinuityRole.Unknown})return (i,split!,branch);
            }
            return null;
        }

        private ComponentLaneOutcome OutletLane(DirectedRoad edge,PrefabLanePath path,
            (int Index,LaneContinuityPoint Point,LaneContinuityBranch Branch)? split,ComponentLaneOutcome? origin,
            DirectedRoad? mainPassage,IReadOnlyDictionary<NativeRoadKey,DirectedRoad> originals)
        {
            double length=Length(path);
            var entry=At(edge,path,0);var midpoint=At(edge,path,length/2);var exit=At(edge,path,length);
            var lane=new ComponentLaneOutcome(path.EntryOrder,path.ExitOrder,ComponentLaneFate.Continues,false,0,
                midpoint,exit,null,exit,null){CurveIndices=path.CurveIndices,MidpointDistanceFromExitMeters=midpoint.OffsetMeters};
            if(!continuity.TryGetValue(path.PrefabUid,out var facts))
                return lane with{Fate=ComponentLaneFate.Unresolved,Issue=new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,
                    "Native outlet lane geometry is unavailable."){QueryAnchor=entry.Position}};
            if(facts.MapIssue is {} invalid)return lane with{Fate=ComponentLaneFate.Unresolved,Issue=invalid};
            if(split is {} change)
            {
                if(change.Branch.MapIssue is not null||change.Branch.Role==LaneContinuityRole.Unknown)
                    return lane with{Fate=ComponentLaneFate.Unresolved,Issue=change.Branch.MapIssue??change.Point.MapIssue
                        ??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"Native outlet split is unresolved."){QueryAnchor=entry.Position}};
                int index=change.Index;
                var terminal=At(edge,path,CurveOffset(path,index));
                return lane with{Fate=ComponentLaneFate.Born,InComponent=true,Terminal=terminal,
                    Turn=ConvertTurn(change.Branch.Turn),TurnPrefabUid=path.PrefabUid,TurnSharedCurveIndex=change.Point.SharedCurveIndex,
                    TurnBranchCurveIndex=change.Branch.CurveIndex,StructuralConstraintId=$"lane-split:{path.PrefabUid:X16}:{change.Point.SharedCurveIndex}",
                    TerminalDistanceFromExitMeters=terminal.OffsetMeters};
            }
            if(origin is null)return lane;
            if(origin.Issue is not null||origin.Fate==ComponentLaneFate.Unresolved)
                return lane with{Fate=ComponentLaneFate.Unresolved,Issue=origin.Issue};
            if(origin.Fate is not (ComponentLaneFate.Dies or ComponentLaneFate.Separates))return lane;
            var (turn,issue)=SoftOutletTurn(mainPassage!,edge);
            return lane with{Fate=origin.Fate==ComponentLaneFate.Dies?ComponentLaneFate.Born:ComponentLaneFate.Joins,
                Terminal=Restore(origin.Terminal,originals),SoftAction=Restore(origin.SoftAction,originals),
                Turn=turn,Issue=issue,TerminalDistanceFromExitMeters=-origin.TerminalDistanceFromExitMeters,
                SoftActionDistanceFromExitMeters=-origin.SoftActionDistanceFromExitMeters,StructuralConstraintId=origin.StructuralConstraintId};
        }

        private static (ComponentTurn? Turn,MapQueryIssue? Issue) SoftOutletTurn(DirectedRoad main,DirectedRoad side)
        {
            var mainPort=main.Points[^1];
            var sidePort=side.Points[^1];
            bool HorizontalFinite(Vector3 point)=>float.IsFinite(point.X)&&float.IsFinite(point.Z);
            if(main.NativeExitTangent is not {} tangent||!HorizontalFinite(tangent)||(tangent.X==0&&tangent.Z==0)
                ||!HorizontalFinite(mainPort)||!HorizontalFinite(sidePort))
                return (null,new MapQueryFailure(MapQueryFailureKind.InvalidData,
                    $"Soft outlet {side.Id}: invalid downstream port coordinates or main outlet tangent."){QueryAnchor=side.Points[0]});
            // PPD path tangents point in the forward travel direction. With the
            // map's X/Z axes, a positive determinant is to the driver's right.
            double cross=tangent.X*((double)sidePort.Z-mainPort.Z)-tangent.Z*((double)sidePort.X-mainPort.X);
            if(cross==0)
                return (null,new MapQueryFailure(MapQueryFailureKind.Ambiguous,
                    $"Soft outlet {side.Id}: downstream outlet is collinear with the main outlet heading."){QueryAnchor=side.Points[0]});
            return (new(cross>0?NavigationSide.Right:NavigationSide.Left,ComponentTurnKind.Forward),null);
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
                    if(branch?.Role==LaneContinuityRole.Dead)return new(edge,path,branch,merge,true,null);
                }
                if(i+1<path.CurveIndices.Count)
                {
                    var split=facts.Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==curve);
                    var branch=split?.Branches.FirstOrDefault(b=>b.CurveIndex==path.CurveIndices[i+1]);
                    if(branch is {Role:LaneContinuityRole.Unknown})return new(edge,path,null,null,false,branch.MapIssue??split?.MapIssue);
                    if(branch is {Role:LaneContinuityRole.Newborn})
                    {
                        // An issue-free split with every branch explicitly non-surviving
                        // proves where this lane's identity ends in the reverse graph.
                        bool noMain=reverse&&split!.MapIssue is null&&split.Branches.All(b=>
                            b.Role==LaneContinuityRole.Newborn&&b.MapIssue is null);
                        return new(edge,path,null,null,false,reverse?branch.MapIssue:null)
                            {NoMainSplitIndex=noMain?i+1:null};
                    }
                }
            }
            return new(edge,path,null,null,true,null);
        }

        private ComponentLaneOutcome Walk(DirectedRoad initial,int order,Local[] mains,bool mainEntrance)
        {
            var family=prefabs[initial.LanePaths[0].PrefabUid].Where(e=>e.StartNodeUid==initial.StartNodeUid)
                .SelectMany(e=>e.LanePaths.Where(p=>p.EntryOrder==order).Select(p=>Inspect(e,p))).ToArray();
            var chosen=Choose(family);
            return Trace(initial,chosen?.Lane??family.First().Lane,chosen,mains,mainEntrance,
                initialIssue:family.Select(p=>p.Issue).OfType<MapQueryIssue>().FirstOrDefault());
        }

        private ComponentLaneOutcome Trace(DirectedRoad initial,PrefabLanePath path,Local? chosen,Local[] mains,bool mainEntrance,
            IReadOnlyList<int>? upstreamReferenceOrders=null,MapQueryIssue? initialIssue=null)
        {
            double initialLength=Length(path);var originEdge=chosen?.Edge??initial;
            var midpoint=At(originEdge,path,initialLength/2);var exit=At(originEdge,path,initialLength);
            var trace=new List<Segment>();
            if(upstreamReferenceOrders is null)trace.Add(new(originEdge,path,-initialLength,initialLength));
            ComponentLaneOutcome Result(ComponentLaneFate fate,bool current,double distance,ComponentBoundary? terminal=null,
                ComponentBoundary? lifecycle=null,ComponentTurn? turn=null,MapQueryIssue? issue=null)
            {
                double actionDistance=Math.Max(0,distance-ComponentContinuity.AdvanceMeters);
                if(reverse&&fate is ComponentLaneFate.Dies or ComponentLaneFate.Separates)
                    actionDistance=Math.Min(Math.Max(0,distance),ComponentContinuity.AdvanceMeters);
                return new(path.EntryOrder,path.ExitOrder,fate,current,distance,midpoint,exit,terminal,lifecycle,
                    trace.Count==0?exit:At(trace,actionDistance),turn,issue)
                {
                    MidpointDistanceFromExitMeters=-initialLength/2,
                    LifecycleDistanceFromExitMeters=current?0:distance+(lifecycle is not null&&terminal is not null&&lifecycle.EdgeId==terminal.EdgeId?lifecycle.OffsetMeters-terminal.OffsetMeters:0),
                    SoftActionDistanceFromExitMeters=actionDistance,
                    StructuralConstraintId=fate==ComponentLaneFate.Dies&&chosen?.Death is not null
                        ?Constraint(chosen):fate==ComponentLaneFate.Separates&&terminal is not null
                            ?$"{(reverse?"component-joining":"component-separation")}:{chosen!.Lane.PrefabUid:X16}:{chosen.Edge.StartNodeUid:X16}:{chosen.Edge.EndNodeUid:X16}":null,
                    TurnPrefabUid=fate==ComponentLaneFate.Dies?chosen!.Lane.PrefabUid:null,
                    TurnSharedCurveIndex=fate==ComponentLaneFate.Dies?chosen!.Merge!.SharedCurveIndex:null,
                    TurnBranchCurveIndex=fate==ComponentLaneFate.Dies?chosen!.Death!.CurveIndex:null
                };
            }
            ComponentLaneOutcome Missing(string reason,ulong node,MapQueryIssue? issue=null)
                =>Result(ComponentLaneFate.Unresolved,false,0,issue:(issue??missing?.Invoke(node)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,reason)) with{QueryAnchor=upstreamReferenceOrders is null?initial.Points[0]:exit.Position});
            double distance=-initialLength;bool current=true;
            var references=mains.Select(m=>new Reference(m,-Length(m.Lane))).ToArray();
            MapQueryIssue? referenceIssue=null;
            if(upstreamReferenceOrders is not null)
            {
                distance=0;current=false;
                var terminal=Advance(exit.EndNodeUid,path.ExitOrder,NativeLaneGraph.Prefab(initial)!,
                    upstreamReferenceOrders.Select(order=>(Order:order,Distance:0d)).ToArray());
                if(terminal is not null)return terminal;
            }
            else
            {
                if(chosen is null)return Missing("原生车道没有唯一身份延续",initial.StartNodeUid,
                    initialIssue??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"原生车道没有唯一身份延续"));
                // The scored main entrance needs only its local identity proof.
                // Its downstream graph is a dependency of side-lane classification,
                // not of the already confirmed main continuation in this component.
                if(mainEntrance&&chosen.Death is null)
                    return Result(ComponentLaneFate.Continues,true,0);
            }
            // A repeated edge is another measured occurrence. Positive lengths
            // make the finite-graph traversal advance to its distance budget.
            while(true)
            {
                token.ThrowIfCancellationRequested();
                var localLane=chosen!;
                if(mains.Length==0&&localLane.Death is null)return Missing("主线资格已丢失，无法确认最近主线",localLane.Edge.StartNodeUid,
                    referenceIssue??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"主线资格已丢失，无法确认最近主线"));
                double referenceEnd=references.Length>0&&references.All(r=>r.Local.Death is not null)
                    ?references.Max(r=>LaneTraceDistance.Advance(r.StartDistance,DeathOffset(r.Local))):double.PositiveInfinity;
                double limit=Math.Min(ComponentContinuity.WindowMeters,referenceEnd);
                bool sameDeath=localLane.Death is not null&&references.Any(r=>r.Local.Death is not null
                    &&r.Local.Lane.PrefabUid==localLane.Lane.PrefabUid
                    &&r.Local.Merge!.SharedCurveIndex==localLane.Merge!.SharedCurveIndex);
                bool sameBoundary=references.Any(r=>r.Local.Death is not null&&r.Local.Lane.PrefabUid==localLane.Lane.PrefabUid
                    &&r.Local.Merge!.SharedCurveIndex==localLane.Lane.CurveIndices[0]);
                if(distance>limit&&!sameDeath&&!sameBoundary)
                    return referenceIssue is null?Result(ComponentLaneFate.Continues,false,limit)
                        :Missing("主线参照结束，其余候选身份尚未确认",localLane.Edge.StartNodeUid,referenceIssue);
                var nearest=mains.Select(m=>(Main:m,Distance:Vector3.DistanceSquared(localLane.Lane.Points[0],m.Lane.Points[0])))
                    .OrderBy(p=>p.Distance).ToArray();
                if(nearest.Length>1&&Math.Abs(nearest[0].Distance-nearest[1].Distance)<1e-6
                    &&nearest[0].Main.Lane.ExitNodeUid!=nearest[1].Main.Lane.ExitNodeUid)
                    return Missing("最近主线不唯一",localLane.Edge.StartNodeUid,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"最近主线不唯一"));
                if(nearest.Length>0&&localLane.Lane.ExitNodeUid!=nearest[0].Main.Lane.ExitNodeUid)
                {
                    var terminal=At(localLane.Edge,localLane.Lane,0);
                    // The upstream trace establishes a boundary. A soft outlet
                    // uses its own downstream ports to describe the direction.
                    if(reverse)return Result(ComponentLaneFate.Separates,current,distance,terminal,terminal);
                    var (direction,directionIssue)=Toward(localLane.Lane,nearest[0].Main.Lane);
                    return Result(ComponentLaneFate.Separates,current,distance,terminal,terminal,direction,
                        directionIssue);
                }
                if(localLane.Death is {} dead)
                {
                    double local=DeathOffset(localLane);
                    double at=distance+local;
                    // The side's real death at the same origin wins over the
                    // observation range ending there.
                    if(at<=ComponentContinuity.WindowMeters&&(at<=referenceEnd||sameDeath))
                        return Result(ComponentLaneFate.Dies,current,at,At(localLane.Edge,localLane.Lane,local),
                        At(localLane.Edge,localLane.Lane,Length(localLane.Lane)),reverse?null:ConvertTurn(dead.Turn),
                        reverse?localLane.Merge!.MapIssue:dead.MapIssue);
                    if(referenceEnd>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,at);
                }
                if(referenceEnd<=ComponentContinuity.WindowMeters)
                    return referenceIssue is null?Result(ComponentLaneFate.Continues,false,referenceEnd)
                        :Missing("主线参照结束，其余候选身份尚未确认",localLane.Edge.StartNodeUid,referenceIssue);
                distance=LaneTraceDistance.Advance(distance,Length(localLane.Lane));
                if(distance>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,distance);
                ulong node=localLane.Edge.EndNodeUid;int lane=localLane.Lane.ExitOrder;
                var mainEnds=references.Where(r=>r.Local.Death is null&&r.Local.Lane.ExitNodeUid==node)
                    .Select(r=>(Order:r.Local.Lane.ExitOrder,Distance:LaneTraceDistance.Advance(r.StartDistance,Length(r.Local.Lane)))).ToArray();
                var advance=Advance(node,lane,NativeLaneGraph.Prefab(localLane.Edge)!,mainEnds);
                if(advance is not null)return advance;
            }

            ComponentLaneOutcome? Advance(ulong node,int lane,string previousPrefab,(int Order,double Distance)[] mainEnds)
            {
                var mainOrders=mainEnds.Select(r=>r.Order).ToHashSet();
                string? previousRoad=null;
                while(true)
                {
                    var next=(starts.GetValueOrDefault(node)??[]).Where(e=>NativeLaneGraph.Prefab(e)!=previousPrefab&&e.Id!=previousRoad).ToArray();
                    var roads=next.Where(e=>NativeLaneGraph.Prefab(e) is null).ToArray();
                    if(roads.Length==0)break;
                    if(roads.Length!=1||next.Length!=1)return Missing("相接普通道路不唯一",node,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接普通道路不唯一"));
                    var road=roads[0];double length=LaneTraceDistance.Positive(RouteGeometry.Length(road),$"Road {road.Id}");
                    mainEnds=mainEnds.Select(r=>(r.Order,Distance:LaneTraceDistance.Advance(r.Distance,length))).ToArray();
                    trace.Add(new(road,null,distance,length));distance=LaneTraceDistance.Advance(distance,length);node=road.EndNodeUid;previousRoad=road.Id;
                    if(distance>ComponentContinuity.WindowMeters)return Result(ComponentLaneFate.Continues,false,distance);
                }
                var component=(starts.GetValueOrDefault(node)??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null&&NativeLaneGraph.Prefab(e)!=previousPrefab).ToArray();
                if(component.Length==0)return Missing("500米窗口内相接组件尚未加载",node);
                if(component.Any(e=>e.Junction?.IsJunction==true))
                    return Result(ComponentLaneFate.Continues,false,distance);
                if(component.Select(e=>NativeLaneGraph.Prefab(e)).Distinct().Count()!=1)
                    return Missing("相接预制件不唯一",node,new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接预制件不唯一"));
                var options=component.SelectMany(e=>e.LanePaths.Where(p=>p.EntryOrder==lane).Select(p=>Inspect(e,p))).ToArray();
                chosen=Choose(options);
                if(chosen is null&&upstreamReferenceOrders is not null&&referenceIssue is null
                    &&options.All(o=>!o.IdentityPreserved&&o.Issue is null))
                {
                    // Discarded side branches may have lost identity before the confirmed stop.
                    var stopped=options.Where(o=>o.NoMainSplitIndex.HasValue).ToArray();
                    if(stopped.Length>0)
                    {
                        double boundary=stopped.Min(o=>CurveOffset(o.Lane,o.NoMainSplitIndex!.Value));
                        return Result(ComponentLaneFate.Continues,false,
                            Math.Min(ComponentContinuity.WindowMeters,LaneTraceDistance.Advance(distance,boundary)));
                    }
                }
                if(chosen is null)return Missing("相接组件车道没有唯一原生延续",node,
                    options.Select(p=>p.Issue).OfType<MapQueryIssue>().FirstOrDefault()??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"相接组件车道没有唯一原生延续"));
                var mainCandidates=component.SelectMany(e=>e.LanePaths.Where(p=>mainOrders.Contains(p.EntryOrder)).Select(p=>Inspect(e,p))).ToArray();
                // A later confirmed death does not resolve an earlier uncertain reference.
                referenceIssue??=mainCandidates.Select(m=>m.Issue).OfType<MapQueryIssue>().FirstOrDefault();
                references=mainCandidates.Where(p=>p.IdentityPreserved&&p.Issue is null)
                    .SelectMany(m=>mainEnds.Where(r=>r.Order==m.Lane.EntryOrder).Select(r=>new Reference(m,r.Distance))).ToArray();
                mains=references.Select(r=>r.Local).ToArray();
                trace.Add(new(chosen.Edge,chosen.Lane,distance,Length(chosen.Lane)));current=false;
                return null;
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
        private string Constraint(Local local)
        {
            var point=local.Merge!;
            return $"{(reverse?"lane-split":"lane-merge")}:{local.Lane.PrefabUid:X16}:{point.SharedCurveIndex}";
        }
        private double DeathOffset(Local local)
        {
            // A repeated physical position elsewhere on the polyline is not
            // this native merge visit. Use the shared curve's sampled boundary.
            int shared=local.Merge!.SharedCurveIndex;
            ValidateDeathVisit(local.Lane,shared);
            int index=local.Lane.CurveIndices.ToList().IndexOf(shared);
            return CurveOffset(local.Lane,index);
        }
        private static double CurveOffset(PrefabLanePath lane,int index)
            =>new LanePolyline(lane.Points).Along[lane.CurveStartPointIndices[index]];
        [System.Diagnostics.Conditional("DEBUG")]
        private static void ValidateDeathVisit(PrefabLanePath lane,int shared)
        {
            if(lane.CurveStartPointIndices.Count!=lane.CurveIndices.Count||lane.CurveIndices.Count(i=>i==shared)!=1
                ||lane.CurveStartPointIndices.Any(i=>i<0||i>=lane.Points.Count))
                throw new NavigationContractException($"Native lane {lane.PrefabUid:X16}:{lane.EntryOrder}->{lane.ExitOrder} must identify one sampled visit of shared curve {shared}.");
        }
    }
}
