using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Proves where a dedicated exit lane opens on the loaded route.</summary>
public static class DedicatedLaneOpening
{
    private const double MaximumLead=1000;
    public static double? Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        double[] offsets,int fork,ManeuverKind kind)
    {
        var selected=route[fork];
        bool right=kind==ManeuverKind.KeepRight;
        var alternatives=graph.Where(e=>e.StartNodeUid==selected.StartNodeUid&&e.EndNodeUid!=selected.EndNodeUid)
            .SelectMany(e=>e.LanePaths).ToArray();
        if(alternatives.Length==0)return null;
        var dedicated=selected.LanePaths.Where(p=>p.EntryLaneCount>1).Where(p=>right?p.EntryOrder==p.EntryLaneCount-1:p.EntryOrder==0)
            .Where(p=>!alternatives.Any(other=>other.EntryLane==p.EntryLane)).ToArray();
        if(dedicated.Length!=1)return null;
        var chosen=dedicated[0];int order=chosen.EntryOrder,count=chosen.EntryLaneCount;
        for(int i=fork-1;i>=0 && offsets[fork]-offsets[i]<=MaximumLead;i--)
        {
            var edge=route[i];
            if(edge.Roundabout is not null)return null;
            if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal))continue;
            // No other route choice may lie between the opening and the dedicated exit.
            if(graph.Any(e=>e.StartNodeUid==edge.StartNodeUid&&e.EndNodeUid!=edge.EndNodeUid
                &&e.Id.StartsWith("prefab:",StringComparison.Ordinal)))return null;
            var paths=edge.LanePaths.Where(p=>p.ExitOrder==order&&p.ExitLaneCount==count).ToArray();
            if(paths.Length!=1)return null;
            var path=paths[0];
            if(path.ExitLaneCount==path.EntryLaneCount+1 && (right?order==count-1:order==0))
            {
                var preserved=edge.LanePaths.Where(p=>p.EntryLane==path.EntryLane&&p.ExitLane!=path.ExitLane).ToArray();
                if(preserved.Length!=1)return null;
                int common=0;var other=preserved[0];
                while(common<Math.Min(path.CurveIndices.Count,other.CurveIndices.Count)
                    &&path.CurveIndices[common]==other.CurveIndices[common])common++;
                if(common==0||common>=path.CurveIndices.Count||path.CurveStartPointIndices.Count!=path.CurveIndices.Count)return null;
                var opening=path.Points[path.CurveStartPointIndices[common]];
                var projection=new LanePolyline(edge.Points).Project(opening);
                if(projection.Error>15)return null;
                double lead=offsets[fork]-offsets[i]-projection.Along;
                return lead>0&&lead<=MaximumLead?lead:null;
            }
            if(path.EntryLaneCount!=count||path.EntryOrder!=order)return null;
            order=path.EntryOrder;count=path.EntryLaneCount;
        }
        return null;
    }
}
