using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Map;

/// <summary>The directed node sequence is topology; remaining distance, ETA and refresh timestamps are not.</summary>
public static class RouteStructureIdentity
{
    public static ulong[] Nodes(RouteSnapshot route)
    {
        var nodes = route.Nodes.Where((n, i) => i == 0 || n != route.Nodes[i - 1]).ToArray();
        if (nodes.Length < 2) return [];
        bool descending = nodes.Zip(nodes.Skip(1)).All(p => p.First.DistanceToEndMeters > p.Second.DistanceToEndMeters);
        bool ascending = nodes.Zip(nodes.Skip(1)).All(p => p.First.DistanceToEndMeters < p.Second.DistanceToEndMeters);
        if (!descending && !ascending) return [];
        if (!descending) Array.Reverse(nodes);
        return nodes.Select(n => n.Uid).ToArray();
    }
}
