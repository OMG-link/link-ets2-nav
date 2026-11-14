namespace LinkEts2Nav.Core;

/// <summary>Indexes the loaded directed native graph. No route, vehicle, or virtual lane-change edges.</summary>
public sealed class NativeLaneGraph
{
    public IReadOnlyList<DirectedRoad> Edges {get;}
    private readonly Dictionary<ulong,IReadOnlyList<DirectedRoad>> starts;
    private readonly Dictionary<string,IReadOnlyList<DirectedRoad>> prefabs;
    public NativeLaneGraph(IReadOnlyList<DirectedRoad> edges)
    {
        Edges=Array.AsReadOnly(edges.ToArray());
        starts=edges.GroupBy(e=>e.StartNodeUid).ToDictionary(g=>g.Key,g=>(IReadOnlyList<DirectedRoad>)Array.AsReadOnly(g.ToArray()));
        prefabs=edges.Where(e=>Prefab(e) is not null).GroupBy(e=>Prefab(e)!)
            .ToDictionary(g=>g.Key,g=>(IReadOnlyList<DirectedRoad>)Array.AsReadOnly(g.ToArray()),StringComparer.Ordinal);
    }
    public IReadOnlyList<DirectedRoad> From(ulong node)=>starts.GetValueOrDefault(node)??[];
    public IReadOnlyList<DirectedRoad> InPrefab(DirectedRoad edge)
        =>Prefab(edge) is {} id?prefabs.GetValueOrDefault(id)??[]:[];
    public static string? Prefab(DirectedRoad edge)=>edge.Id.StartsWith("prefab:",StringComparison.Ordinal)?edge.Id.Split(':')[1]:null;
}
