using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Proves where a dedicated exit lane opens on the loaded route.</summary>
public static class DedicatedLaneOpening
{
    public static double? Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        double[] offsets,int fork,IReadOnlyList<int> dedicatedOrders)
    {
        var selected=route[fork];
        var proofs=new List<double>();
        foreach(int order in dedicatedOrders.Distinct())
        {
            var chosen=selected.LanePaths.Where(p=>p.EntryOrder==order).ToArray();
            if(chosen.Length==0||chosen.Select(p=>p.EntryLaneCount).Distinct().Count()!=1)continue;
            if(Trace(order,chosen[0].EntryLaneCount) is {} lead)proofs.Add(lead);
        }
        return proofs.Count==0?null:proofs.Max();

        double? Trace(int order,int count)
        {
            for(int i=fork-1;i>=0;i--)
            {
                var edge=route[i];
                if(edge.Roundabout is not null)return null;
                if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal))continue;
                // A different exit choice is a separate action, not this lane's opening.
                if(graph.Any(e=>e.StartNodeUid==edge.StartNodeUid&&e.EndNodeUid!=edge.EndNodeUid
                    &&e.Id.StartsWith("prefab:",StringComparison.Ordinal)))return null;
                var paths=edge.LanePaths.Where(p=>p.ExitOrder==order&&p.ExitLaneCount==count).ToArray();
                if(paths.Length==0&&i+1<fork&&!route[i+1].Id.StartsWith("prefab:",StringComparison.Ordinal)
                    &&route[i+1].StartNodeUid==edge.EndNodeUid)
                {
                    // A side entrance can add an independent parallel lane. Once the
                    // shared ordinary-road corridor starts, the lane planner permits
                    // lateral changes within that same directional output group.
                    var joined=graph.Where(e=>e.EndNodeUid==edge.EndNodeUid&&e.StartNodeUid!=edge.StartNodeUid)
                        .SelectMany(e=>e.LanePaths).Where(p=>p.ExitLaneCount==count).ToArray();
                    bool ownGroup=edge.LanePaths.Count>0&&edge.LanePaths.All(p=>p.ExitLaneCount==count);
                    bool independentlyAdded=joined.Any(p=>p.ExitOrder==order)
                        &&!joined.Any(p=>edge.LanePaths.Any(q=>q.ExitLane==p.ExitLane));
                    if(ownGroup&&independentlyAdded)
                    {
                        double lead=offsets[fork]-offsets[i+1];
                        if(lead>0)return lead;
                    }
                }
                if(paths.Length!=1)return null;
                var path=paths[0];
                if(path.ExitLaneCount>path.EntryLaneCount)
                {
                    var preserved=edge.LanePaths.Where(p=>p.EntryLane==path.EntryLane&&p.ExitLane!=path.ExitLane).ToArray();
                    var positions=new List<double>();
                    foreach(var other in preserved)
                    {
                        int common=0;
                        while(common<Math.Min(path.CurveIndices.Count,other.CurveIndices.Count)
                            &&path.CurveIndices[common]==other.CurveIndices[common])common++;
                        if(common==0||common>=path.CurveIndices.Count||path.CurveStartPointIndices.Count!=path.CurveIndices.Count)continue;
                        var opening=path.Points[path.CurveStartPointIndices[common]];
                        var projection=new LanePolyline(edge.Points).Project(opening);
                        if(projection.Error<=15)positions.Add(offsets[fork]-offsets[i]-projection.Along);
                    }
                    double lead=positions.Where(p=>p>0).DefaultIfEmpty().Max();
                    return lead>0?lead:null;
                }
                // Native lane paths express the mapping across interfaces; lane numbers
                // are local to each interface and need not be equal on the two ends.
                order=path.EntryOrder;count=path.EntryLaneCount;
            }
            return null;
        }
    }
}
