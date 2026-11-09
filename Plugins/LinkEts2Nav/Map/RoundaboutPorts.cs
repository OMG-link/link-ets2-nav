using System.Numerics;
namespace LinkEts2Nav.Map;

public sealed record RoundaboutEntryPort(ulong NodeUid, double RingAlong);
public sealed record RoundaboutExitPort(ulong ExternalItemUid, ulong ExternalNodeUid, ulong BoundaryNodeUid, double RingAlong, double DepartureToBoundaryMeters = 0)
{
    public Vector3 DeparturePosition { get; init; }
}

/// <summary>Maps physical ports to a representative ring using directed shortest connector paths.</summary>
public static class RoundaboutPorts
{
    public static (RoundaboutEntryPort[] Entries, RoundaboutExitPort[] Exits, double Length) Build(
        IReadOnlyDictionary<RoundaboutCurveKey, RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring, IReadOnlyList<ulong> members)
    {
        var owners = members.ToHashSet(); var ringSet = ring.ToHashSet();
        bool Inside(RoundaboutCurve c) => c.PrefabUid is { } owner ? owners.Contains(owner) : ringSet.Contains(c.Key);
        var local = graph.Values.Where(Inside).ToArray();
        var reverse = local.SelectMany(c => c.Next.Select(n => (From: c.Key, To: n))).GroupBy(p => p.To)
            .ToDictionary(g => g.Key, g => g.Select(p => p.From).ToArray());
        double Length(RoundaboutCurve c) => c.Points.Zip(c.Points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));
        var lengths = graph.Values.ToDictionary(c => c.Key, Length);
        var phases = new Dictionary<RoundaboutCurveKey, double>(); double total = 0;
        foreach (var key in ring) { phases[key] = total; total += lengths[key]; }
        var entryBest = new Dictionary<RoundaboutCurveKey, (double Cost, double Phase)>();
        var queue = new PriorityQueue<(RoundaboutCurveKey Key, double Phase), double>();
        foreach (var key in ring) { entryBest[key] = (0, phases[key]); queue.Enqueue((key, phases[key]), 0); }
        while (queue.TryDequeue(out var current, out double cost))
        {
            if (cost > entryBest[current.Key].Cost) continue;
            foreach (var previous in reverse.GetValueOrDefault(current.Key) ?? [])
            {
                double nextCost = cost + lengths[previous];
                if (entryBest.TryGetValue(previous, out var known) && known.Cost <= nextCost) continue;
                entryBest[previous] = (nextCost, current.Phase); queue.Enqueue((previous, current.Phase), nextCost);
            }
        }
        var entries = local.Where(c => c.EntryNode != 0 && entryBest.ContainsKey(c.Key)).GroupBy(c => c.EntryNode)
            .Select(g => g.MinBy(c => entryBest[c.Key].Cost)!).Select(c => new RoundaboutEntryPort(c.EntryNode, entryBest[c.Key].Phase)).ToArray();
        var best = new Dictionary<RoundaboutCurveKey, double>();
        var pending = new PriorityQueue<(RoundaboutCurveKey Key, double Phase, ulong Boundary, double BoundaryCost), double>();
        // Sources are the ends of ring curves: a departure branches after its source curve.
        foreach (var key in ring)
        { best[key] = 0; pending.Enqueue((key, (phases[key] + lengths[key]) % total, 0, 0), 0); }
        var departurePositions=ring.ToDictionary(k=>(phases[k]+lengths[k])%total,k=>graph[k].Points[^1]);
        var exits = new Dictionary<(ulong, ulong, ulong), (double Cost, RoundaboutExitPort Port)>();
        while (pending.TryDequeue(out var current, out double cost))
        {
            if (cost > best[current.Key]) continue;
            var curve = graph[current.Key];
            ulong boundary = curve.PrefabUid is { } owner && owners.Contains(owner) && curve.ExitNode != 0
                ? curve.ExitNode : current.Boundary;
            double boundaryCost = boundary != current.Boundary ? cost : current.BoundaryCost;
            if (curve.Next.Count == 0 && curve.TerminalExit && curve.ExitNode != 0 && boundary != 0)
            {
                var key = (curve.Key.ItemUid, curve.ExitNode, boundary);
                if (!exits.TryGetValue(key, out var known) || cost < known.Cost)
                    exits[key] = (cost, new(curve.Key.ItemUid, curve.ExitNode, boundary, current.Phase, boundaryCost));
            }
            foreach (var next in curve.Next)
            {
                if (!graph.TryGetValue(next, out var target)) continue;
                if (target.PrefabUid is { } external && !owners.Contains(external))
                {
                    var key = (external, target.EntryNode, boundary);
                    if (boundary != 0 && (!exits.TryGetValue(key, out var known) || cost < known.Cost))
                        exits[key] = (cost, new(external, target.EntryNode, boundary, current.Phase, boundaryCost));
                    continue;
                }
                double nextCost = cost + lengths[next];
                if (best.TryGetValue(next, out double knownCost) && knownCost <= nextCost) continue;
                best[next] = nextCost; pending.Enqueue((next, current.Phase, boundary, boundaryCost), nextCost);
            }
        }
        return (entries, exits.Values.Select(p => p.Port with {DeparturePosition=departurePositions[p.Port.RingAlong]}).ToArray(), total);
    }
}
