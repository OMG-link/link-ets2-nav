using System.Globalization;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Collapses each selected ring passage, preserving its actual route geometry and length.</summary>
public static class RoundaboutRouteAdapter
{
    public static IReadOnlyList<DirectedRoad> Apply(IReadOnlyList<DirectedRoad> route,
        IReadOnlyDictionary<ulong, RoundaboutStructure> annotations)
    {
        var result = new List<DirectedRoad>();
        for (int i = 0; i < route.Count; i++)
        {
            if (!TryOwner(route[i], out ulong uid) || !annotations.TryGetValue(uid, out var ring))
            { result.Add(route[i]); continue; }
            int last = i;
            while (last + 1 < route.Count && TryOwner(route[last + 1], out var nextUid)
                && annotations.TryGetValue(nextUid, out var nextRing) && nextRing.Id == ring.Id) last++;
            var entry = ring.EntryPorts.SingleOrDefault(p => p.NodeUid == route[i].StartNodeUid);
            var exitMatches = ring.ExitPorts.Where(p => p.BoundaryNodeUid == route[last].EndNodeUid)
                .GroupBy(p => (p.ExternalItemUid, p.ExternalNodeUid)).ToArray();
            if (entry is null || exitMatches.Length != 1 || ring.RingLength <= 0)
            { result.AddRange(route.Skip(i).Take(last - i + 1)); i = last; continue; }
            double Ahead(double phase)
            { double d = (phase - entry.RingAlong + ring.RingLength) % ring.RingLength; return d < .01 || ring.RingLength - d < .01 ? 0 : d; }
            var ordered = ring.ExitPorts.GroupBy(p => (p.ExternalItemUid, p.ExternalNodeUid))
                .Select(g => (g.Key, Along: g.Min(p => Ahead(p.RingAlong)))).OrderBy(p => p.Along).ToArray();
            bool ambiguous = ordered.Zip(ordered.Skip(1)).Any(p => Math.Abs(p.First.Along - p.Second.Along) < .01);
            int ordinal = Array.FindIndex(ordered, p => p.Key == exitMatches[0].Key) + 1;
            if (ambiguous || ordinal == 0)
            { result.AddRange(route.Skip(i).Take(last - i + 1)); i = last; continue; }
            var points = route.Skip(i).Take(last - i + 1).SelectMany(e => e.Points).ToArray();
            result.Add(new("roundabout:" + ring.Id, route[i].StartNodeUid, route[last].EndNodeUid, points)
            { Roundabout = new(ring.Id, ordinal, exitMatches[0].Min(p => p.DepartureToBoundaryMeters)) });
            i = last;
        }
        return result;
    }
    private static bool TryOwner(DirectedRoad road, out ulong uid)
    {
        var parts = road.Id.Split(':');
        return ulong.TryParse(parts.Length == 4 && parts[0] == "prefab" ? parts[1] : road.Id,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uid);
    }
}
