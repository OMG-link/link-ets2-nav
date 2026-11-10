using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

internal readonly record struct LimitEdgeKey(string Id, ulong Start, ulong End)
{
    public static LimitEdgeKey Of(DirectedRoad road) => new(road.Id, road.StartNodeUid, road.EndNodeUid);
}
public sealed record SpeedLimitLocation(DirectedRoad Road, double AlongMeters,
    PostedLimitState Posted, LimitRoad? Metadata);

/// <summary>Immutable published interval/spatial index. Querying never traverses preceding roads.</summary>
public sealed class PostedLimitIndex
{
    private const int CellSize = 100;
    private readonly Dictionary<(int X,int Z), DirectedRoad[]> cells;
    private readonly IReadOnlyDictionary<LimitEdgeKey, IReadOnlyList<PostedLimitInterval>> intervals;
    private readonly IReadOnlyDictionary<LimitEdgeKey, LimitRoad?> metadata;
    private readonly DirectedPathMatcher matcher = new(horizontalToleranceMeters:12, verticalToleranceMeters:3);
    internal PostedLimitIndex(IReadOnlyList<DirectedRoad> graph,
        IReadOnlyDictionary<LimitEdgeKey,IReadOnlyList<PostedLimitInterval>> intervals,
        IReadOnlyDictionary<LimitEdgeKey,LimitRoad?> metadata)
    {
        this.intervals=intervals; this.metadata=metadata;
        var buckets=new Dictionary<(int,int),HashSet<DirectedRoad>>();
        foreach(var edge in graph)
        foreach(var (a,b) in edge.Points.Zip(edge.Points.Skip(1)))
        {
            int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(a,b)/50));
            for(int i=0;i<=steps;i++)
            {
                var p=Vector3.Lerp(a,b,(float)i/steps);var key=((int)Math.Floor(p.X/CellSize),(int)Math.Floor(p.Z/CellSize));
                if(!buckets.TryGetValue(key,out var items))buckets[key]=items=[];
                items.Add(edge);
            }
        }
        cells=buckets.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
    }
    public SpeedLimitLocation? Locate(Vector3 position,Vector3 direction)
    {
        int x=(int)Math.Floor(position.X/CellSize),z=(int)Math.Floor(position.Z/CellSize);
        var nearby=new HashSet<DirectedRoad>();
        for(int dx=-1;dx<=1;dx++)for(int dz=-1;dz<=1;dz++)
            if(cells.TryGetValue((x+dx,z+dz),out var edges))nearby.UnionWith(edges);
        var candidates=nearby.Select(e=>(Road:e,Along:matcher.ProjectDistance(position,direction,[e]),
            Projection:SpeedLimitGeometry.Nearest(position,e)))
            .Where(p=>p.Along.HasValue && p.Projection is {Error:<12,Vertical:<=3})
            .OrderBy(p=>p.Projection!.Value.Error).ThenBy(p=>p.Road.Id.StartsWith("prefab:")).ToArray();
        if(candidates.Length==0)return null;
        var chosen=candidates[0];
        // Do not conflate physically separate parallel roads. Adjacent items with a shared UID are continuous.
        if(candidates.Skip(1).Any(c=>c.Road.Id!=chosen.Road.Id
            && !c.Road.Id.StartsWith("prefab:") && !chosen.Road.Id.StartsWith("prefab:")
            && c.Projection!.Value.Error-chosen.Projection!.Value.Error<2
            && c.Road.StartNodeUid!=chosen.Road.EndNodeUid && c.Road.EndNodeUid!=chosen.Road.StartNodeUid))return null;
        var key=LimitEdgeKey.Of(chosen.Road);double along=chosen.Along!.Value;
        var range=intervals[key].LastOrDefault(r=>r.FromMeters<=along+.001);
        return new(chosen.Road,along,range?.State??new(),metadata.GetValueOrDefault(key));
    }
}
