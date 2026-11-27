using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

internal readonly record struct LimitEdgeKey(string Id,ulong Start,ulong End)
{
    public static LimitEdgeKey Of(DirectedRoad road)=>new(road.Id,road.StartNodeUid,road.EndNodeUid);
}

/// <summary>A sign projected onto one directed road/PPD connection, not a propagated speed interval.</summary>
public sealed record ProjectedLimitSign(string SignId,double AlongMeters,Vector3 Position,LimitAction Action);

internal sealed class DirectedSignEvents(IReadOnlyList<PrefabLanePath> lanes)
{
    private readonly List<ProjectedLimitSign> passage=[];
    private Dictionary<PrefabLanePath,List<ProjectedLimitSign>>? byLane;

    public void Add(ProjectedLimitSign sign,PrefabLanePath? lane=null)
    {
        if(lane is null){passage.Add(sign);return;}
        byLane??=new(ReferenceEqualityComparer.Instance);
        if(!byLane.TryGetValue(lane,out var signs))byLane[lane]=signs=[];
        signs.Add(sign);
    }
    public void Sort()
    {
        static int Compare(ProjectedLimitSign a,ProjectedLimitSign b)=>a.AlongMeters!=b.AlongMeters
            ?a.AlongMeters.CompareTo(b.AlongMeters):StringComparer.Ordinal.Compare(a.SignId,b.SignId);
        passage.Sort(Compare);
        if(byLane is not null)foreach(var signs in byLane.Values)signs.Sort(Compare);
    }
    public bool Contains(PrefabLanePath? lane)=>lane is null||lanes.Contains(lane,ReferenceEqualityComparer.Instance);
    public IReadOnlyList<ProjectedLimitSign> On(PrefabLanePath? lane)=>lane is null?passage:byLane?.GetValueOrDefault(lane)??[];
}

public sealed class ProjectedSignIndex
{
    private readonly IReadOnlyDictionary<LimitEdgeKey,DirectedSignEvents> events;
    private readonly IReadOnlyDictionary<LimitEdgeKey,LimitRoad?> metadata;
    internal ProjectedSignIndex(IReadOnlyDictionary<LimitEdgeKey,DirectedSignEvents> events,
        IReadOnlyDictionary<LimitEdgeKey,LimitRoad?> metadata)
    {this.events=events;this.metadata=metadata;}
    public IReadOnlyList<ProjectedLimitSign> On(DirectedRoad road)=>events.GetValueOrDefault(LimitEdgeKey.Of(road))?.On(road.ObservedLane)??[];
    internal bool ShareNativeStation(DirectedRoad road,string first,string second)
    {
        if(road.ObservedLane is not null||road.LanePaths.Count<2)return true;
        if(!events.TryGetValue(LimitEdgeKey.Of(road),out var edge))return false;
        foreach(var lane in road.LanePaths)
        {
            var signs=edge.On(lane);
            var a=signs.FirstOrDefault(s=>s.SignId==first&&!s.Action.Unsupported);
            var b=signs.FirstOrDefault(s=>s.SignId==second&&!s.Action.Unsupported);
            if(a is not null&&b is not null&&Math.Abs(a.AlongMeters-b.AlongMeters)<=SignProjector.InterfaceToleranceMeters)return true;
        }
        return false;
    }
    internal LimitRoad? Context(DirectedRoad road)=>metadata.GetValueOrDefault(LimitEdgeKey.Of(road));
    internal bool Contains(DirectedRoad road)=>events.TryGetValue(LimitEdgeKey.Of(road),out var edge)&&edge.Contains(road.ObservedLane);
}
