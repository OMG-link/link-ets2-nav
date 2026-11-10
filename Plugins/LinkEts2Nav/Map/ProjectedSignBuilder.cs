using System.Globalization;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Background sign projection only. No traversal, merge, or road-wide temporary speed state.</summary>
public static class ProjectedSignBuilder
{
    public static SpeedLimitData Build(SpeedLimitData data,IReadOnlyList<DirectedRoad> prefabPaths,CancellationToken token=default)
    {
        if(data.Error is not null)return data;
        var roads=data.Roads.Values.SelectMany(r=>
            (r.ForwardTraffic?new[]{r.Geometry}:Array.Empty<DirectedRoad>()).Concat(r.ReverseTraffic
                ?new[]{r.Geometry with{StartNodeUid=r.Geometry.EndNodeUid,EndNodeUid=r.Geometry.StartNodeUid,Points=r.Geometry.Points.Reverse().ToArray()}}
                :Array.Empty<DirectedRoad>()));
        var graph=roads.Concat(prefabPaths).Where(e=>e.Points.Count>=2).DistinctBy(LimitEdgeKey.Of).ToArray();
        var projection=SignProjector.Build(data,graph,token);
        var events=new Dictionary<LimitEdgeKey,IReadOnlyList<ProjectedLimitSign>>();
        var metadata=new Dictionary<LimitEdgeKey,LimitRoad?>();
        var starts=graph.GroupBy(e=>e.StartNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        var ends=graph.GroupBy(e=>e.EndNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        for(int i=0;i<graph.Length;i++)
        {
            token.ThrowIfCancellationRequested();var edge=graph[i];var key=LimitEdgeKey.Of(edge);
            events[key]=projection.Events[i].Select(e=>new ProjectedLimitSign(e.SignId,e.At,
                SpeedLimitGeometry.PointAt(edge,e.At),e.Action)).ToArray();
            if(ulong.TryParse(edge.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var uid))
                metadata[key]=data.Roads.GetValueOrDefault(uid);
            else
            {
                var context=(ends.GetValueOrDefault(edge.StartNodeUid)??[]).Concat(starts.GetValueOrDefault(edge.EndNodeUid)??[])
                    .Select(e=>(Edge:e,Uid:ulong.TryParse(e.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var id)?(ulong?)id:null))
                    .Where(p=>p.Uid.HasValue&&data.Roads.ContainsKey(p.Uid.Value)).ToArray();
                metadata[key]=context.Length==0?null:data.Roads[context[0].Uid!.Value] with{
                    Geometry=edge,SpeedClass=data.Roads[context[0].Uid!.Value].ClassFor(context[0].Edge),DirectionalClasses=null};
            }
        }
        return data with{SignProjections=new(events,metadata),
            RegionIndex=new(data.Regions),Diagnostics=projection.Diagnostics};
    }
}
