using System.Numerics;
using System.Runtime.CompilerServices;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Map;

/// <summary>Nearby graded PPD curves from one loaded map snapshot and the lane paths that use them.</summary>
internal sealed class PrefabLanePositionIndex
{
    private sealed record Curve(ulong PrefabUid, IReadOnlyList<Vector3> Points, Vector3 Minimum, Vector3 Maximum,
        HashSet<PrefabLanePath> PhysicalLanes)
    {
        public HashSet<PrefabLanePath> Lanes { get; } = new(ReferenceEqualityComparer.Instance);
        public float SamplingError { get; } = ChordError(Points);
    }
    private readonly record struct Fit(Curve Curve, Vector3 Point, Vector2 Heading, double Error);

    private const float CellSize = 64;
    private const float SearchRadius = 12;
    internal const float LaneSeparation = .5f;
    private readonly Dictionary<(int X, int Z), List<Curve>> cells = [];
    private readonly Lazy<(ILookup<ulong, PrefabLanePath> Inputs, ILookup<ulong, PrefabLanePath> Outputs)> ports;
    private readonly ConditionalWeakTable<IReadOnlyList<DirectedRoad>, RouteLaneVisits> routeVisits = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<DirectedRoad>, PrefabLanePositionIndex> snapshots = new();

    public static PrefabLanePositionIndex For(IReadOnlyList<DirectedRoad> edges)
        => snapshots.GetValue(edges, static mapEdges => new(mapEdges));

    private PrefabLanePositionIndex(IReadOnlyList<DirectedRoad> edges)
    {
        ports = new(() =>
        {
            var lanes = edges.SelectMany(edge => edge.LanePaths);
            return (lanes.ToLookup(lane => lane.EntryNodeUid), lanes.ToLookup(lane => lane.ExitNodeUid));
        });
        var curves = new Dictionary<(ulong Owner, int Index, Vector3 Start, Vector3 End), Curve>();
        var physical = new Dictionary<(ulong Owner, int Index), HashSet<PrefabLanePath>>();
        int unsplit = -1;
        foreach (var lane in edges.SelectMany(edge => edge.LanePaths))
        {
            var starts = lane.CurveStartPointIndices;
            if (lane.Points.Count < 2) continue;
            if (starts.Count == 0 || starts.Count != lane.CurveIndices.Count)
            {
                // Older lane data can provide a whole path without individual curve spans.
                Add(lane, lane.CurveIndices.Count == 1 ? lane.CurveIndices[0] : unsplit--, 0, lane.Points.Count - 1);
                continue;
            }
            for (int i = 0; i < starts.Count; i++)
            {
                // Native samples include the end of each curve before the next start.
                // Two-point chains may represent the common joint with just one point.
                int end = i + 1 == starts.Count ? lane.Points.Count - 1
                    : starts[i + 1] - starts[i] > 1 ? starts[i + 1] - 1 : starts[i + 1];
                Add(lane, lane.CurveIndices[i], starts[i], end);
            }
        }

        void Add(PrefabLanePath lane, int curveIndex, int first, int last)
        {
            if (first < 0 || last >= lane.Points.Count || first >= last) return;
            // A PPD curve can serve several exits. Its height can vary with each graded passage.
            var key = (lane.PrefabUid, curveIndex, lane.Points[first], lane.Points[last]);
            var identity = (lane.PrefabUid, curveIndex);
            if (!physical.TryGetValue(identity, out var passages))
                physical.Add(identity, passages = new(ReferenceEqualityComparer.Instance));
            passages.Add(lane);
            if (!curves.TryGetValue(key, out var curve))
            {
                var points = first == 0 && last == lane.Points.Count - 1 ? lane.Points
                    : lane.Points.Skip(first).Take(last - first + 1).ToArray();
                var min = points.Aggregate(Vector3.Min);
                var max = points.Aggregate(Vector3.Max);
                curve = new(lane.PrefabUid, points, min, max, passages);
                curves.Add(key, curve);
                for (int x = Cell(min.X - SearchRadius); x <= Cell(max.X + SearchRadius); x++)
                for (int z = Cell(min.Z - SearchRadius); z <= Cell(max.Z + SearchRadius); z++)
                {
                    if (!cells.TryGetValue((x,z), out var nearby)) cells[(x,z)] = nearby = [];
                    nearby.Add(curve);
                }
            }
            curve.Lanes.Add(lane);
        }
    }

    private static int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellSize);

    internal static float CoordinateResolution(Vector3 position)
    {
        static float Step(float x) => MathF.BitIncrement(Math.Abs(x)) - Math.Abs(x);
        return Math.Max(.001f, new Vector3(Step(position.X), Step(position.Y), Step(position.Z)).Length());
    }

    private static float ChordError(IReadOnlyList<Vector3> points)
    {
        float error = 0;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            var chord = points[i + 1] - points[i - 1];
            if (chord.LengthSquared() <= .000001f) continue;
            float t = Math.Clamp(Vector3.Dot(points[i] - points[i - 1],chord) / chord.LengthSquared(), 0, 1);
            error = Math.Max(error,Vector3.Distance(points[i],points[i - 1] + chord * t));
        }
        return error;
    }

    public ILookup<ulong, PrefabLanePath> Ports(bool inputs) => inputs ? ports.Value.Inputs : ports.Value.Outputs;

    public (double OnRoute, double OffRoute, float SamplingError) Errors(Vector3 position, Vector3 heading,
        IReadOnlyList<DirectedRoad> path, int firstVisit, DirectedPathMatcher matcher)
    {
        var absent = (double.PositiveInfinity,double.PositiveInfinity,0f);
        if (!cells.TryGetValue((Cell(position.X), Cell(position.Z)), out var nearby)) return absent;
        var visits = routeVisits.GetValue(path, static route => new(route));
        if (!visits.Matches(path))
        {
            visits = new(path);
            routeVisits.AddOrUpdate(path, visits);
        }
        Fit? best = null;
        foreach (var curve in nearby)
        {
            if (!visits.IsLive(curve.Lanes, firstVisit) || Project(curve) is not {} fit) continue;
            if (best is null || fit.Error < best.Value.Error) best = fit;
        }
        if (best is not {} chosen) return absent;
        double offRoute = double.PositiveInfinity;
        var direction = Vector2.Normalize(new(heading.X,heading.Z));
        foreach (var curve in nearby)
        {
            // Another exit may grade the very same physical PPD curve differently.
            // Only the live route's sampled height can support it; another grade cannot veto it.
            if (visits.IsLive(curve.PhysicalLanes, firstVisit) || Project(curve) is not {} rival) continue;
            if (rival.Error <= chosen.Error + LaneSeparation
                && FitsJunctionPassage(position, direction, chosen, rival, visits, firstVisit)) continue;
            offRoute = Math.Min(offRoute, rival.Error);
        }
        return (chosen.Error, offRoute, chosen.Curve.SamplingError);

        Fit? Project(Curve curve)
        {
            if (position.X < curve.Minimum.X - SearchRadius || position.X > curve.Maximum.X + SearchRadius
                || position.Z < curve.Minimum.Z - SearchRadius || position.Z > curve.Maximum.Z + SearchRadius) return null;
            return matcher.ProjectPolyline(position, heading, curve.Points) is {} p ? new(curve,p.Position,p.Heading,p.Error) : null;
        }
    }

    private static bool FitsJunctionPassage(Vector3 position, Vector2 travel, Fit route, Fit rival, RouteLaneVisits visits, int firstVisit)
    {
        if (route.Curve.PrefabUid != rival.Curve.PrefabUid) return false;
        float separation = Vector3.Distance(route.Point,rival.Point);
        var horizontal = new Vector2(position.X,position.Z);
        float routeError = Vector2.Distance(horizontal,new(route.Point.X,route.Point.Z));
        float rivalError = Vector2.Distance(horizontal,new(rival.Point.X,rival.Point.Z));
        if (separation > LaneSeparation || routeError > LaneSeparation) return false;
        float resolution = CoordinateResolution(position);
        if (routeError > rivalError + resolution || separation > resolution && routeError + resolution >= rivalError)
            return false;
        // Compare local tangents in travel direction. One degree is the parallel-heading
        // tolerance already used when connecting native lane ports.
        const float oneDegree = .017453f;
        float routeHeadingError = Vector2.Distance(travel,route.Heading);
        float rivalHeadingError = Vector2.Distance(travel,rival.Heading);
        if (routeHeadingError > rivalHeadingError + oneDegree) return false;
        if (routeHeadingError <= oneDegree && routeHeadingError + oneDegree < rivalHeadingError) return true;
        foreach (var a in route.Curve.Lanes)
        foreach (var b in rival.Curve.Lanes)
        {
            if (visits.IsLive(a,firstVisit) && (a.EntryNodeUid == b.EntryNodeUid && a.EntryLane == b.EntryLane
                || a.ExitNodeUid == b.ExitNodeUid && a.ExitLane == b.ExitLane
                || a.CurveIndices.Any(b.CurveIndices.Contains))) return true;
        }
        return false;
    }

    private sealed class RouteLaneVisits
    {
        private readonly DirectedRoad[] edges;
        private readonly Dictionary<PrefabLanePath,int> last = new(ReferenceEqualityComparer.Instance);

        public RouteLaneVisits(IReadOnlyList<DirectedRoad> route)
        {
            edges = route.ToArray();
            for (int i = 0; i < edges.Length; i++)
            foreach (var lane in edges[i].LanePaths) last[lane] = i;
        }

        public bool Matches(IReadOnlyList<DirectedRoad> route)
        {
            if (edges.Length != route.Count) return false;
            for (int i = 0; i < edges.Length; i++)
                if (!ReferenceEquals(edges[i], route[i])) return false;
            return true;
        }

        public bool IsLive(HashSet<PrefabLanePath> lanes, int firstVisit)
        {
            foreach (var lane in lanes)
                if (IsLive(lane,firstVisit)) return true;
            return false;
        }
        public bool IsLive(PrefabLanePath lane, int firstVisit) => last.TryGetValue(lane, out int index) && index >= firstVisit;
    }
}
