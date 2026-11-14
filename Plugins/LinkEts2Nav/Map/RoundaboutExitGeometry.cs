using System.Numerics;
namespace LinkEts2Nav.Map;

/// <summary>Exit geometry is independent of the representative ring and never filters physical ports.</summary>
internal static class RoundaboutExitGeometry
{
    public static IReadOnlyDictionary<ulong,IReadOnlyList<RoundaboutExitBranch>> Build(RoundaboutBoundary region,CancellationToken token)
    {
        var graph=region.Curves.Where(c=>c.PrefabUid is not null||c.TrafficKnown&&c.ProvenTraffic).ToDictionary(c=>c.Key);
        var reverse=graph.Values.SelectMany(c=>c.Next.Where(graph.ContainsKey).Select(next=>(From:c.Key,To:next)))
            .GroupBy(p=>p.To).ToDictionary(g=>g.Key,g=>g.Select(p=>p.From).ToArray());
        var reachable=graph.Keys.ToDictionary(k=>k,_=>new HashSet<ulong>());
        // One reverse traversal per physical exit, not per lane, path or ring.
        // All sets and queues belong only to this construction.
        foreach(var exit in region.Exits)
        {
            var queue=new Queue<RoundaboutCurveKey>(exit.Curves);
            while(queue.TryDequeue(out var current))
            {
                token.ThrowIfCancellationRequested();
                if(!reachable[current].Add(exit.NodeUid))continue;
                foreach(var previous in reverse.GetValueOrDefault(current)??[])queue.Enqueue(previous);
            }
        }
        return region.Exits.ToDictionary(p=>p.NodeUid,p=>
        {
            // A circulation curve can reach several exits. Its exit-only successor
            // starts the actual departure branch, on whichever lane ring it belongs to.
            var starts=graph.Values.Where(c=>reachable[c.Key].Count==1&&reachable[c.Key].Contains(p.NodeUid)
                &&(reverse.GetValueOrDefault(c.Key)??[]).Any(previous=>reachable[previous].Count>1))
                .OrderBy(c=>c.Key.ItemUid).ThenBy(c=>c.Key.CurveIndex).Select(c=>c.Key);
            // Retain native output starts as well: a direct entrance/exit slip path
            // or declared component need not share a multi-exit circulation core.
            // The selected path chooses its earliest matching departure curve.
            return (IReadOnlyList<RoundaboutExitBranch>)starts.Concat(p.Curves).Distinct()
                .Select(k=>new RoundaboutExitBranch(k,graph[k].Points[0])).ToArray();
        });
    }
}
