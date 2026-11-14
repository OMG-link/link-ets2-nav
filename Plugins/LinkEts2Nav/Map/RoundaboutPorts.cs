using System.Numerics;
namespace LinkEts2Nav.Map;

public sealed record RoundaboutEntryPort(ulong NodeUid, double RingAlong)
{
    public double Bearing { get; init; }
}
public sealed record RoundaboutExitPort(ulong BoundaryNodeUid, double RingAlong, double DepartureToBoundaryMeters = 0)
{
    public Vector3 DeparturePosition { get; init; }
    public double Bearing { get; init; }
    public IReadOnlyList<RoundaboutCurveKey> Branches { get; init; } = [];
}

/// <summary>Maps physical ports to a representative ring using directed shortest connector paths.</summary>
public static class RoundaboutPorts
{
    public static (RoundaboutEntryPort[] Entries, RoundaboutExitPort[] Exits, double Length) Build(
        IReadOnlyDictionary<RoundaboutCurveKey, RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring, IReadOnlyList<ulong> members, CancellationToken token = default)
    {
        var boundary=RoundaboutBoundary.Build(graph,ring,members);
        var local=boundary.Curves;
        var localGraph = local.ToDictionary(c => c.Key);
        var entryPositions=boundary.Entries.ToDictionary(p=>p.NodeUid,p=>p.Position);
        var exitPositions=boundary.Exits.ToDictionary(p=>p.NodeUid,p=>p.Position);
        // Length weighting avoids bias from uneven sampling densities.
        double weight = 0, centerX = 0, centerZ = 0, area = 0;
        var origin = graph[ring[0]].Points[0];
        foreach (var key in ring)
        {
            var points = graph[key].Points;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                double length = Vector3.Distance(a, b);
                weight += length; centerX += ((double)a.X + b.X) * .5 * length;
                centerZ += ((double)a.Z + b.Z) * .5 * length;
                area += (double)(a.X - origin.X) * (b.Z - origin.Z) - (double)(b.X - origin.X) * (a.Z - origin.Z);
            }
        }
        centerX /= weight; centerZ /= weight;
        double direction = area >= 0 ? 1 : -1;
        double Bearing(Vector3 p) => direction * Math.Atan2(p.Z - centerZ, p.X - centerX);
        var reverse = local.SelectMany(c => c.Next.Where(localGraph.ContainsKey).Select(n => (From: c.Key, To: n))).GroupBy(p => p.To)
            .ToDictionary(g => g.Key, g => g.Select(p => p.From).ToArray());
        double Length(RoundaboutCurve c) => c.Points.Zip(c.Points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));
        var lengths = local.ToDictionary(c => c.Key, Length);
        var phases = new Dictionary<RoundaboutCurveKey, double>(); double total = 0;
        foreach (var key in ring) { phases[key] = total; total += lengths[key]; }
        var entryBest = new Dictionary<RoundaboutCurveKey, (double Cost, double Phase)>();
        var queue = new PriorityQueue<(RoundaboutCurveKey Key, double Phase), double>();
        foreach (var key in ring) { entryBest[key] = (0, phases[key]); queue.Enqueue((key, phases[key]), 0); }
        while (queue.TryDequeue(out var current, out double cost))
        {
            token.ThrowIfCancellationRequested();
            if (cost > entryBest[current.Key].Cost) continue;
            foreach (var previous in reverse.GetValueOrDefault(current.Key) ?? [])
            {
                double nextCost = cost + lengths[previous];
                if (entryBest.TryGetValue(previous, out var known) && known.Cost <= nextCost) continue;
                entryBest[previous] = (nextCost, current.Phase); queue.Enqueue((previous, current.Phase), nextCost);
            }
        }
        var entries = local.Where(c => entryPositions.ContainsKey(c.EntryNode) && entryBest.ContainsKey(c.Key)).GroupBy(c => c.EntryNode)
            .Select(g => g.MinBy(c => entryBest[c.Key].Cost)!).Select(c => new RoundaboutEntryPort(c.EntryNode, entryBest[c.Key].Phase) { Bearing = Bearing(entryPositions[c.EntryNode]) }).ToArray();
        var best = new Dictionary<RoundaboutCurveKey, double>();
        var pending = new PriorityQueue<(RoundaboutCurveKey Key, double Phase), double>();
        // Sources are the ends of ring curves: a departure branches after its source curve.
        foreach (var key in ring)
        { best[key] = 0; pending.Enqueue((key, (phases[key] + lengths[key]) % total), 0); }
        var departurePositions=ring.ToDictionary(k=>(phases[k]+lengths[k])%total,k=>graph[k].Points[^1]);
        var exits = new Dictionary<ulong, (double Cost, RoundaboutExitPort Port)>();
        var branches = new Dictionary<ulong, List<RoundaboutCurveKey>>();
        while (pending.TryDequeue(out var current, out double cost))
        {
            token.ThrowIfCancellationRequested();
            if (cost > best[current.Key]) continue;
            var curve = localGraph[current.Key];
            if (exitPositions.ContainsKey(curve.ExitNode))
            {
                if (!branches.TryGetValue(curve.ExitNode, out var lanes)) branches[curve.ExitNode] = lanes = [];
                lanes.Add(curve.Key);
                if (!exits.TryGetValue(curve.ExitNode, out var known) || cost < known.Cost)
                    exits[curve.ExitNode] = (cost, new(curve.ExitNode, current.Phase, cost));
                continue;
            }
            foreach (var next in curve.Next)
            {
                if (!localGraph.ContainsKey(next)) continue;
                double nextCost = cost + lengths[next];
                if (best.TryGetValue(next, out double knownCost) && knownCost <= nextCost) continue;
                best[next] = nextCost; pending.Enqueue((next, current.Phase), nextCost);
            }
        }
        return (entries, exits.OrderBy(p => p.Key).Select(p => p.Value.Port with
        {
            DeparturePosition = departurePositions[p.Value.Port.RingAlong],
            Bearing = Bearing(exitPositions[p.Key]),
            Branches = branches[p.Key].OrderBy(k => k.ItemUid).ThenBy(k => k.CurveIndex).ToArray()
        }).ToArray(), total);
    }
}
