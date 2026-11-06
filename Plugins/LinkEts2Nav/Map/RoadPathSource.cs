using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>Builds provable directed road paths; unknown prefab geometry is never invented.</summary>
public sealed class RoadPathSource
{
    private readonly Dictionary<(ulong, ulong), Road> links = new();
    private readonly Dictionary<ulong, DirectedRoad> samples = new();
    private readonly Dictionary<ulong, List<Road>> incidentRoads = new();
    private readonly HashSet<(ulong, ulong)> ambiguous = new();
    private readonly Road[] roads;
    private readonly DirectedPathMatcher matcher = new();
    private RouteSnapshot? cachedRoute;
    private Vector3 cachedPosition;
    private List<List<DirectedRoad>> cachedChunks = [];

    public RoadPathSource(GameMap map)
    {
        roads = map.MapItems.Values.OfType<Road>().ToArray();
        foreach (var road in roads)
        {
            if (road.Node is null || road.ForwardNode is null) continue;
            AddIncidentRoad(road.Node.Uid, road);
            AddIncidentRoad(road.ForwardNode.Uid, road);
            AddLink((road.Node.Uid, road.ForwardNode.Uid), road);
            AddLink((road.ForwardNode.Uid, road.Node.Uid), road);
        }
    }

    private void AddIncidentRoad(ulong uid, Road road)
    {
        if (!incidentRoads.TryGetValue(uid, out var connected))
            incidentRoads[uid] = connected = new();
        connected.Add(road);
    }

    private void AddLink((ulong, ulong) key, Road road)
    {
        if (!links.TryAdd(key, road)) ambiguous.Add(key);
    }

    public IReadOnlyList<DirectedRoad> GetPath(Vector3 position, Vector3 heading, RouteSnapshot route,
        RouteAvailability availability)
    {
        if (availability == RouteAvailability.None) return CurrentRoad(position, heading);
        if (availability != RouteAvailability.Available) return [];
        var matches = GetRouteChunks(position, route)
            .Where(chunk => IsPositionOnPath(position, chunk) && matcher.ProjectDistance(position, heading, chunk).HasValue)
            .ToArray();
        return matches.Length == 1 ? matches[0] : [];
    }

    /// <summary>
    /// Road distance is available only within one continuous route chunk. Across an unknown
    /// prefab, fallback is allowed only when both positions independently match a unique
    /// directed road on the ordered game route and the camera chunk is strictly later.
    /// </summary>
    public CameraCandidate MatchCamera(Vector3 position, Vector3 heading, MonitoredCamera camera,
        RouteSnapshot route, RouteAvailability availability)
    {
        var direct = Vector3.Distance(position, camera.MonitoringPosition);
        CameraCandidate Unknown() => new(camera.Id, CameraApproach.Unknown, direct);
        if (availability == RouteAvailability.None)
        {
            var path = CurrentRoad(position, heading);
            return IsPositionOnPath(camera.MonitoringPosition, path)
                ? matcher.Match(position, heading, camera, path, false) : Unknown();
        }
        if (availability != RouteAvailability.Available) return Unknown();
        var chunks = GetRouteChunks(position, route);
        var playerChunks = chunks.Select((path, index) => (Path: path, Index: index))
            .Where(p => IsPositionOnPath(position, p.Path) && matcher.ProjectDistance(position, heading, p.Path).HasValue)
            .ToArray();
        var cameraChunks = chunks.Select((path, index) => (Path: path, Index: index))
            .Where(p => IsPositionOnPath(camera.MonitoringPosition, p.Path)
                && matcher.ProjectDistance(camera.MonitoringPosition, camera.TravelDirection, p.Path).HasValue)
            .ToArray();
        if (playerChunks.Length != 1 || cameraChunks.Length != 1) return Unknown();
        var player = playerChunks[0];
        var monitor = cameraChunks[0];
        if (player.Index == monitor.Index)
            return matcher.Match(position, heading, camera, player.Path, true);
        return new(camera.Id, monitor.Index > player.Index ? CameraApproach.RouteAhead : CameraApproach.Behind, direct);
    }

    private List<List<DirectedRoad>> GetRouteChunks(Vector3 position, RouteSnapshot route)
    {
        if (ReferenceEquals(cachedRoute, route) && cachedPosition == position) return cachedChunks;
        cachedRoute = route;
        cachedPosition = position;
        cachedChunks = [];
        if (route.Nodes.Count < 2) return cachedChunks;
        var nodes = route.Nodes.ToArray();
        bool decreasing = nodes.Zip(nodes.Skip(1)).All(p => p.First.DistanceToEndMeters > p.Second.DistanceToEndMeters);
        bool increasing = nodes.Zip(nodes.Skip(1)).All(p => p.First.DistanceToEndMeters < p.Second.DistanceToEndMeters);
        if (!decreasing && !increasing) return cachedChunks;
        if (!decreasing) Array.Reverse(nodes);
        List<DirectedRoad>? chunk = null;
        for (int i = 1; i < nodes.Length; i++)
        {
            var key = (nodes[i - 1].Uid, nodes[i].Uid);
            if (ambiguous.Contains(key) || !links.TryGetValue(key, out var road) || !Near(road, position))
            { chunk = null; continue; }
            var directed = Orient(road, key.Item1);
            if (chunk is null) { chunk = new(); cachedChunks.Add(chunk); }
            chunk.Add(directed);
        }
        return cachedChunks;
    }

    /// <summary>Uses all nearby roads so a parallel non-route road cannot borrow route geometry.</summary>
    public bool IsPositionOnPath(Vector3 position, IReadOnlyList<DirectedRoad> path)
    {
        if (path.Count == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;
        var candidates = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Score: Distance(position, [Orient(r, r.Node.Uid)])))
            .Where(p => p.Score < 12).OrderBy(p => p.Score).Take(2).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3)
            return false;
        var selectedId = candidates[0].Road.Uid.ToString("X16");
        return path.Any(road => StringComparer.Ordinal.Equals(road.Id, selectedId));
    }

    private IReadOnlyList<DirectedRoad> CurrentRoad(Vector3 position, Vector3 heading)
    {
        var candidates = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Sample: Orient(r, r.Node.Uid)))
            .Select(p => (p.Road, p.Sample, Score: Distance(position, [p.Sample])))
            .Where(p => p.Score < 12).OrderBy(p => p.Score).Take(2).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3) return [];
        var candidate = candidates[0];
        var nearest = candidate.Sample.Points.Zip(candidate.Sample.Points.Skip(1))
            .OrderBy(p => SegmentDistance(position, p.First, p.Second)).First();
        var tangent = Vector3.Normalize(nearest.Second - nearest.First);
        var start = Vector3.Dot(tangent, heading) >= 0 ? candidate.Road.Node.Uid : candidate.Road.ForwardNode.Uid;
        var current = Orient(candidate.Road, start);
        var path = new List<DirectedRoad> { current };
        var visited = new HashSet<ulong> { candidate.Road.Uid };
        double coveredAhead = DistanceToEnd(position, current);
        while (coveredAhead < 1000 && path.Count < 128)
        {
            if (!incidentRoads.TryGetValue(current.EndNodeUid, out var connected)) break;
            var successors = connected.Where(r => r.Uid.ToString("X16") != current.Id).ToArray();
            // Only a unique Road continuation can extend the proof of inevitability.
            if (successors.Length != 1 || !visited.Add(successors[0].Uid)) break;
            var next = Orient(successors[0], current.EndNodeUid);
            if (ambiguous.Contains((next.StartNodeUid, next.EndNodeUid))
                || Vector3.Distance(current.Points[^1], next.Points[0]) > 2) break;
            path.Add(next);
            coveredAhead += PolylineLength(next);
            current = next;
        }
        return path;
    }

    private static double PolylineLength(DirectedRoad road)
        => road.Points.Zip(road.Points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));

    private static double DistanceToEnd(Vector3 position, DirectedRoad road)
    {
        double offset = 0, bestAlong = 0, bestError = double.PositiveInfinity;
        foreach (var (a, b) in road.Points.Zip(road.Points.Skip(1)))
        {
            var delta = b - a;
            var length = delta.Length();
            if (length < .001f) continue;
            var t = Math.Clamp(Vector3.Dot(position - a, delta) / delta.LengthSquared(), 0, 1);
            var error = Vector3.Distance(position, a + delta * t);
            if (error < bestError) { bestError = error; bestAlong = offset + length * t; }
            offset += length;
        }
        return offset - bestAlong;
    }

    private static bool Near(Road road, Vector3 position)
        => Vector3.Distance(road.Node.Position, position) < 2500 || Vector3.Distance(road.ForwardNode.Position, position) < 2500;

    private DirectedRoad Orient(Road road, ulong start)
    {
        if (!samples.TryGetValue(road.Uid, out var sample))
        {
            int count = Math.Clamp((int)Math.Ceiling(road.Length / 8), 16, 256);
            var points = Enumerable.Range(0, count + 1).Select(i => road.InterpolateCurve((float)i / count).Position).ToArray();
            sample = new(road.Uid.ToString("X16"), road.Node.Uid, road.ForwardNode.Uid, points);
            samples.Add(road.Uid, sample);
        }
        return start == sample.StartNodeUid ? sample : new(sample.Id, sample.EndNodeUid, sample.StartNodeUid, sample.Points.Reverse().ToArray());
    }

    private static double Distance(Vector3 point, IReadOnlyList<DirectedRoad> path)
        => path.SelectMany(r => r.Points.Zip(r.Points.Skip(1))).Select(p => SegmentDistance(point, p.First, p.Second))
            .DefaultIfEmpty(double.PositiveInfinity).Min();

    private static double SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var delta = b - a;
        float t = delta.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, delta) / delta.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + delta * t);
    }
}
