using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Preserves all reachable input/output lane slots, independently of sampled shortest paths.</summary>
public static class PrefabLaneTopology
{
    public static IReadOnlyList<LaneConnection>? Build(Prefab prefab, PrefabDescriptor descriptor)
    {
        PrefabPlacement.ValidateTopology(prefab,descriptor);
        PrefabDescriptorInput.AssertCurveIndices(descriptor);
        var nodes=Enumerable.Range(0,prefab.Nodes.Count)
            .Select(i=>descriptor.Nodes[(i+prefab.Origin)%descriptor.Nodes.Count]).ToArray();
        int count=descriptor.NavCurves.Count;
        if(count>8192) return null;
        var result=new List<LaneConnection>();
        for(int from=0;from<nodes.Length;from++)
        for(int lane=0;lane<nodes[from].InputLines.Length;lane++)
        {
            int start=nodes[from].InputLines[lane];
            if(start==-1)continue;
            var seen=ReachableCurves(descriptor,[start]);
            for(int to=0;to<nodes.Length;to++)
            for(int exitLane=0;exitLane<nodes[to].OutputLines.Length;exitLane++)
                if(from!=to && seen.Contains(nodes[to].OutputLines[exitLane]))
                    result.Add(new(prefab.Nodes[from].Uid,lane,prefab.Nodes[to].Uid,exitLane));
        }
        return result.ToArray();
    }

    /// <summary>Checks an unprojected native passage, including returns to the same map interface.</summary>
    internal static bool Reaches(Prefab prefab,PrefabDescriptor descriptor,ulong entry,ulong exit)
    {
        PrefabDescriptorInput.AssertInstance(prefab,descriptor);
        PrefabDescriptorInput.AssertCurveIndices(descriptor);
        var from=ControlOf(prefab,descriptor,entry);var to=ControlOf(prefab,descriptor,exit);
        if(from is null||to is null)return false;
        var seen=ReachableCurves(descriptor,from.InputLines.Where(i=>i!=-1));
        return to.OutputLines.Any(seen.Contains);
    }

    internal static ControlNode? ControlOf(Prefab prefab,PrefabDescriptor descriptor,ulong nodeUid)
    {
        for(int i=0;i<prefab.Nodes.Count;i++)
            if(prefab.Nodes[i].Uid==nodeUid)return descriptor.Nodes[(i+prefab.Origin)%descriptor.Nodes.Count];
        return null;
    }

    internal static HashSet<int> ReachableCurves(PrefabDescriptor descriptor,IEnumerable<int> starts)
    {
        var seen=new HashSet<int>();var pending=new Stack<int>(starts);
        while(pending.TryPop(out int line))
            if(seen.Add(line))foreach(int next in descriptor.NavCurves[line].NextLines)pending.Push(next);
        return seen;
    }
}
