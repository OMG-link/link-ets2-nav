using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

/// <summary>One component index per immutable graph. Lane selection only projects existing facts onto route visits.</summary>
public sealed class ComponentManeuverPlanner
{
    private IReadOnlyList<DirectedRoad>? graph;
    private ComponentContinuityIndex? index;
    public void UseIndex(IReadOnlyList<DirectedRoad> source,ComponentContinuityIndex prepared)
    {graph=source;index=prepared;}
    public IReadOnlyList<MapQueryIssue> Issues {get;private set;}=[];
    public IReadOnlyList<NavigationUnresolvedSpan> Unresolved {get;private set;}=[];
    public IReadOnlyDictionary<int,IReadOnlyList<NavigationManeuver>> Build(IReadOnlyList<DirectedRoad> route,
        IReadOnlyList<DirectedRoad> source,LaneNavigationPlan? selected=null,Func<ulong,MapQueryIssue>? missing=null,Func<int,bool>? excludeVisit=null)
    {
        if(!ReferenceEquals(graph,source))
        {index=ComponentContinuityIndex.Build(new(source),missing);graph=source;}
        var result=new Dictionary<int,IReadOnlyList<NavigationManeuver>>();var issues=new List<MapQueryIssue>();var unresolved=new List<NavigationUnresolvedSpan>();
        var selections=(selected?.Sections??[]).ToDictionary(s=>s.SourceVisitIndex,s=>s.Path);
#if DEBUG
        foreach(var section in selected?.Sections??[])
            if(section.SourceVisitIndex<0||section.SourceVisitIndex>=route.Count||route[section.SourceVisitIndex].Id!=section.EdgeId)
                throw new NavigationContractException($"Selected lane {section.EdgeId} has no matching route visit {section.SourceVisitIndex}.");
#endif
        for(int i=0;i<route.Count;i++)
        {
            if(excludeVisit?.Invoke(i)==true)continue;
            var edge=route[i];if(edge.Junction?.IsJunction==true||edge.Roundabout is not null||edge.ReturnsToEntry)continue;
            var entry=index!.ForEntry(edge.Id);var exit=index.ForExit(edge.Id);
            if(entry is null&&exit is null)
            {
                if((edge.LanePaths.Count==0||edge.LanePaths.Any(p=>p.NativeGeometry is null||LaneContinuity.Analyze(p.NativeGeometry).MapIssue is not null))
                    &&NativeLaneGraph.Prefab(edge) is {} prefab&&(source.Where(e=>NativeLaneGraph.Prefab(e)==prefab&&!e.ReturnsToEntry)
                    .Any(e=>e.StartNodeUid!=edge.StartNodeUid||e.EndNodeUid!=edge.EndNodeUid)
                    ||edge.LaneConnections?.Any(c=>c.EntryNode!=edge.StartNodeUid||c.ExitNode!=edge.EndNodeUid)==true))
                {
                    issues.Add(new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,"Multi-port component lacks native lane geometry; lane continuity is unavailable.")
                        {QueryAnchor=edge.Points.FirstOrDefault()});
                    double start=route.Take(i).Sum(RouteGeometry.Length);unresolved.Add(new(start,start+RouteGeometry.Length(edge)));
                }
                continue;
            }
            var exits=index.Exits.Where(p=>p.PrefabUid==(entry??exit)!.PrefabUid&&p.EntryNodeUid==edge.StartNodeUid).ToArray();
            var projected=ComponentManeuverProjection.Build(route,i,entry is null?[]:[entry],exits,
                selections.GetValueOrDefault(i)??edge.LanePaths.OrderBy(p=>p.EntryOrder).FirstOrDefault());
            result[i]=projected.Actions;issues.AddRange(projected.Issues);unresolved.AddRange(projected.Unresolved);
        }
        // Only visited component issues become guidance barriers; distant graph
        // defects remain in the index instead of blocking unrelated navigation.
        Issues=issues.Distinct().ToArray();Unresolved=unresolved.Distinct().ToArray();return result;
    }
}
