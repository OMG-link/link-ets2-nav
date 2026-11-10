using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

internal readonly record struct LimitEdgeKey(string Id,ulong Start,ulong End)
{
    public static LimitEdgeKey Of(DirectedRoad road)=>new(road.Id,road.StartNodeUid,road.EndNodeUid);
}

/// <summary>A sign projected onto one directed road/PPD connection, not a propagated speed interval.</summary>
public sealed record ProjectedLimitSign(string SignId,double AlongMeters,Vector3 Position,LimitAction Action);
public sealed class ProjectedSignIndex
{
    private readonly IReadOnlyDictionary<LimitEdgeKey,IReadOnlyList<ProjectedLimitSign>> events;
    private readonly IReadOnlyDictionary<LimitEdgeKey,LimitRoad?> metadata;
    internal ProjectedSignIndex(IReadOnlyDictionary<LimitEdgeKey,IReadOnlyList<ProjectedLimitSign>> events,
        IReadOnlyDictionary<LimitEdgeKey,LimitRoad?> metadata)
    {this.events=events;this.metadata=metadata;}
    public IReadOnlyList<ProjectedLimitSign> On(DirectedRoad road)=>events.GetValueOrDefault(LimitEdgeKey.Of(road))??[];
    internal LimitRoad? Context(DirectedRoad road)=>metadata.GetValueOrDefault(LimitEdgeKey.Of(road));
    internal bool Contains(DirectedRoad road)=>events.ContainsKey(LimitEdgeKey.Of(road));
}
