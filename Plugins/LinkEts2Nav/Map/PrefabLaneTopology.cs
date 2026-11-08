using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Preserves all reachable input/output lane slots, independently of sampled shortest paths.</summary>
public static class PrefabLaneTopology
{
    public static IReadOnlyList<LaneConnection>? Build(Prefab prefab, PrefabDescriptor descriptor)
    {
        if (prefab.Nodes.Count != descriptor.Nodes.Count || prefab.Origin >= descriptor.Nodes.Count) return null;
        var nodes=Enumerable.Range(0,prefab.Nodes.Count)
            .Select(i=>descriptor.Nodes[(i+prefab.Origin)%descriptor.Nodes.Count]).ToArray();
        int count=descriptor.NavCurves.Count;
        if(count>8192) return null;
        bool Valid(int index)=>index>=0 && index<count;
        if(nodes.Any(n=>n.InputLines.Concat(n.OutputLines).Any(i=>i < -1 || i>=count))
            ||descriptor.NavCurves.Any(c=>c.NextLines.Any(i=>!Valid(i)))) return null;
        var result=new List<LaneConnection>();
        for(int from=0;from<nodes.Length;from++)
        for(int lane=0;lane<nodes[from].InputLines.Length;lane++)
        {
            int start=nodes[from].InputLines[lane];
            if(!Valid(start))continue;
            var seen=new HashSet<int>();var pending=new Stack<int>();pending.Push(start);
            while(pending.TryPop(out int line))
                if(seen.Add(line))foreach(int next in descriptor.NavCurves[line].NextLines)pending.Push(next);
            for(int to=0;to<nodes.Length;to++)
            for(int exitLane=0;exitLane<nodes[to].OutputLines.Length;exitLane++)
                if(from!=to && seen.Contains(nodes[to].OutputLines[exitLane]))
                    result.Add(new(prefab.Nodes[from].Uid,lane,prefab.Nodes[to].Uid,exitLane));
        }
        return result.ToArray();
    }
}
