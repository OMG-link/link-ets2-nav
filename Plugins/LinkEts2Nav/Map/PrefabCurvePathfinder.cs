using System.Numerics;
using TruckLib.Models.Ppd;
namespace LinkEts2Nav.Map;

/// <summary>Shortest explicitly connected PPD curve chain for a supplied set of entry/exit lanes.</summary>
public static class PrefabCurvePathfinder
{
    public static IReadOnlyList<int> Find(PrefabDescriptor descriptor,IEnumerable<int> starts,IReadOnlySet<int> targets)
    {
        var curves=descriptor.NavCurves;
        bool Valid(int i)=>i>=0&&i<curves.Count&&Finite(curves[i].StartPosition)
            &&Finite(curves[i].EndPosition)&&float.IsFinite(curves[i].Length)&&curves[i].Length>.001f;
        var queue = new PriorityQueue<int, float>();
        var costs = new Dictionary<int, float>();
        var previous = new Dictionary<int, int>();
        foreach (int line in starts.Where(Valid).Distinct())
        { costs[line] = curves[line].Length; previous[line] = -1; queue.Enqueue(line, costs[line]); }
        int found = -1, visited = 0;
        while (queue.TryDequeue(out int line, out float cost) && visited++ < 8192)
        {
            if (cost > costs[line] || cost > 2500) continue;
            if (targets.Contains(line)) { found = line; break; }
            foreach (int next in curves[line].NextLines.Where(Valid))
            {
                if (Vector3.Distance(curves[line].EndPosition, curves[next].StartPosition) > .5f) continue;
                float nextCost = cost + curves[next].Length;
                if (costs.TryGetValue(next, out float known) && known <= nextCost) continue;
                costs[next] = nextCost; previous[next] = line; queue.Enqueue(next, nextCost);
            }
        }
        if (found < 0) return [];
        var chain = new List<int>();
        for (int current = found; current >= 0; current = previous[current])
        { chain.Add(current); if (chain.Count > curves.Count) break; }
        if (chain.Count > curves.Count) return [];
        chain.Reverse();
        return chain.ToArray();
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
