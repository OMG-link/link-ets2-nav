using System.Numerics;

namespace LinkEts2Nav.Map;

public sealed record RoundaboutCurveKey(ulong ItemUid, int CurveIndex);
public sealed record RoundaboutExit(ulong ExternalItemUid, ulong NodeUid, IReadOnlyList<RoundaboutCurveKey> Branches);
public sealed record RoundaboutStructure(string Id, IReadOnlyList<ulong> PrefabUids,
    IReadOnlyList<RoundaboutCurveKey> Ring, IReadOnlyList<Vector3> Geometry,
    double Circularity, IReadOnlyList<RoundaboutExit> Exits)
{
    public IReadOnlyList<RoundaboutEntryPort> EntryPorts { get; init; } = [];
    public IReadOnlyList<RoundaboutExitPort> ExitPorts { get; init; } = [];
    public double RingLength { get; init; }
}

/// <summary>Actual directed PPD curves; road links are explicitly distinguished from prefab curves.</summary>
public sealed record RoundaboutCurve(RoundaboutCurveKey Key, ulong? PrefabUid,
    bool LeftHandTraffic, IReadOnlyList<Vector3> Points, IReadOnlyList<RoundaboutCurveKey> Next,
    ulong EntryNode = 0, ulong ExitNode = 0)
{
    /// <summary>Map endpoint explicitly has no further item; not merely absent from the loaded graph.</summary>
    public bool TerminalExit { get; init; }
}

/// <summary>One short-lived search session per newly constructed sector. Does not own map objects.</summary>
public sealed class RoundaboutBuilder
{
    internal const int MaximumPrefabs = 10;
    // Initial threshold separates the two verified rings from a nearby road-block loop (Q=.805).
    internal const double MinimumCircularity = .85;
    private readonly Dictionary<RoundaboutCurveKey, RoundaboutCurve> graph;
    // Session-only memo 1: deterministic branch choice, including unresolved branches.
    private readonly Dictionary<RoundaboutCurveKey, RoundaboutCurveKey?> successors = new();
    // Session-only memo 2: resolved suffix and ALL owners traversed to reach it.
    // Range-limit and incomplete-data failures are not entered here.
    private readonly Dictionary<RoundaboutCurveKey, (RoundaboutCurveKey[] Ring, ulong[] Owners)> completed = new();

    public RoundaboutBuilder(IEnumerable<RoundaboutCurve> curves)
        => graph = curves.ToDictionary(c => c.Key);

    public RoundaboutStructure? Find(ulong initialPrefab, IEnumerable<RoundaboutCurveKey> entrances,
        CancellationToken cancellationToken = default)
    {
        foreach (var entrance in entrances.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ring = Walk(entrance, cancellationToken);
            if (ring.Length == 0 || !ring.Any(k => graph[k].PrefabUid == initialPrefab)) continue;
            var points = Join(ring);
            double quality = Circularity(points);
            if (quality < MinimumCircularity) continue;
            var members = ring.Select(k => graph[k].PrefabUid).OfType<ulong>().Distinct().Order().ToArray();
            var structure = Assemble(ring, members, points, quality, cancellationToken);
            if (structure is not null) return structure;
        }
        return null;
    }

    private RoundaboutStructure? Assemble(RoundaboutCurveKey[] ring, ulong[] members,
        Vector3[] points, double quality, CancellationToken token)
    {
        var exits = CollectExits(ring, members, token);
        if (exits is null) return null;
        // Multiple lane rings of the same prefab set identify one physical roundabout.
        var id = string.Join("/", members.Select(uid => $"{uid:X16}"));
        var ports = RoundaboutPorts.Build(graph, ring, members);
        return new(id, members, ring, points, quality, exits)
        { EntryPorts=ports.Entries, ExitPorts=ports.Exits, RingLength=ports.Length };
    }

    private RoundaboutCurveKey[] Walk(RoundaboutCurveKey start, CancellationToken token)
    {
        var path = new List<RoundaboutCurveKey>();
        var seen = new Dictionary<RoundaboutCurveKey, int>();
        var prefabs = new HashSet<ulong>();
        var current = start;
        RoundaboutCurveKey[] ring = [];
        bool cacheable = false;
        ulong[] cachedTailOwners = [];
        while (graph.TryGetValue(current, out var curve))
        {
            token.ThrowIfCancellationRequested();
            if (seen.TryGetValue(current, out int index))
            { ring = path.Skip(index).ToArray(); cacheable = true; break; }
            if (completed.TryGetValue(current, out var cached))
            {
                var total = prefabs.Concat(cached.Owners).Distinct().Count();
                if (total <= MaximumPrefabs) { ring = cached.Ring; cachedTailOwners = cached.Owners; cacheable = true; }
                break;
            }
            if (curve.PrefabUid is { } owner && !prefabs.Contains(owner))
            { if (prefabs.Count == MaximumPrefabs) break; prefabs.Add(owner); }
            seen[current] = path.Count; path.Add(current);
            if (!successors.TryGetValue(current, out var next))
                successors[current] = next = Choose(curve);
            if (next is null)
            {
                // A missing referenced curve is incomplete map data, not a permanent dead end.
                cacheable = curve.Next.All(graph.ContainsKey); break;
            }
            current = next;
        }
        // Preserve every owner in the cached suffix, not just owners on the final ring.
        // Otherwise reusing a result could accidentally bypass the ten-prefab limit.
        if (cacheable)
        {
            var suffixOwners = cachedTailOwners.Concat(ring.Select(k => graph[k].PrefabUid).OfType<ulong>()).ToHashSet();
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (graph[path[i]].PrefabUid is { } owner) suffixOwners.Add(owner);
                completed[path[i]] = (ring, suffixOwners.ToArray());
            }
        }
        return ring;
    }

    private RoundaboutCurveKey? Choose(RoundaboutCurve current)
    {
        // Never choose from a partial branch set: a missing branch might be the innermost one.
        if (current.Next.Count == 0 || current.Next.Any(k => !graph.ContainsKey(k))) return null;
        var candidates = current.Next.Select(k => graph[k]).ToArray();
        if (candidates.Length == 1) return candidates[0].Key;
        var tangent = Direction(current.Points, false);
        double distance = Math.Min(3, candidates.Min(c => Length(c.Points)) * .2);
        return candidates.Select(c => (c.Key, Angle: Angle(tangent, Ahead(c.Points, distance) - c.Points[0])))
            .OrderBy(c => current.LeftHandTraffic ? -c.Angle : c.Angle)
            .ThenBy(c => c.Key.ItemUid).ThenBy(c => c.Key.CurveIndex).First().Key;
    }

    private IReadOnlyList<RoundaboutExit>? CollectExits(RoundaboutCurveKey[] ring, ulong[] owners, CancellationToken token)
    {
        var members = owners.ToHashSet(); var visited = new HashSet<RoundaboutCurveKey>();
        var pending = new Stack<RoundaboutCurveKey>(ring);
        var exits = new Dictionary<(ulong, ulong), HashSet<RoundaboutCurveKey>>();
        while (pending.TryPop(out var key))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(key) || !graph.TryGetValue(key, out var curve)) continue;
            if (curve.Next.Count == 0 && curve.ExitNode != 0)
            {
                if (!curve.TerminalExit) return null;
                exits.TryAdd((curve.Key.ItemUid, curve.ExitNode), [key]);
            }
            foreach (var next in curve.Next)
            {
                if (!graph.TryGetValue(next, out var target)) return null;
                if (target.PrefabUid is { } owner && !members.Contains(owner))
                {
                    var exit = (owner, target.EntryNode);
                    if (!exits.TryGetValue(exit, out var branches)) exits[exit] = branches = [];
                    branches.Add(key);
                }
                else pending.Push(next);
            }
        }
        return exits.OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2)
            .Select(p => new RoundaboutExit(p.Key.Item1, p.Key.Item2, p.Value.OrderBy(k => k.ItemUid).ThenBy(k => k.CurveIndex).ToArray())).ToArray();
    }

    private Vector3[] Join(IEnumerable<RoundaboutCurveKey> ring)
        => ring.SelectMany(k => graph[k].Points).ToArray();
    private static double Length(IReadOnlyList<Vector3> points)
        => points.Zip(points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));
    private static Vector3 Direction(IReadOnlyList<Vector3> points, bool start)
        => start ? points[1] - points[0] : points[^1] - points[^2];
    private static Vector3 Ahead(IReadOnlyList<Vector3> points, double distance)
    {
        for (int i = 1; i < points.Count; i++)
        { double length = Vector3.Distance(points[i - 1], points[i]); if (length >= distance && length > 0) return Vector3.Lerp(points[i - 1], points[i], (float)(distance / length)); distance -= length; }
        return points[^1];
    }
    private static double Angle(Vector3 a, Vector3 b) => Math.Atan2(a.X * b.Z - a.Z * b.X, a.X * b.X + a.Z * b.Z);
    public static double Circularity(IReadOnlyList<Vector3> points)
    {
        if (points.Count < 3) return 0;
        double area = 0, perimeter = 0; var origin = points[0];
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i] - origin; var b = points[(i + 1) % points.Count] - origin;
            area += (double)a.X * b.Z - (double)b.X * a.Z;
            perimeter += Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));
        }
        return perimeter > 0 ? 2 * Math.PI * Math.Abs(area) / (perimeter * perimeter) : 0;
    }
}
