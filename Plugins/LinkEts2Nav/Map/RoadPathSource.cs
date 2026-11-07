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
    private readonly Dictionary<(ulong, ulong), DirectedRoad> junctionLinks = new();
    private readonly HashSet<ulong> boundaryNodes = new();
    private readonly DirectedPathMatcher matcher = new();
    private RouteSnapshot? cachedRoute;
    private Vector3 cachedPosition;
    private List<List<DirectedRoad>> cachedChunks = [];
    private IReadOnlyList<DirectedRoad>? navigationGraph;

    public RoadPathSource(GameMap map, IReadOnlyList<DirectedRoad>? junctions = null)
    {
        roads = map.MapItems.Values.OfType<Road>().ToArray();
        foreach (var road in roads)
        {
            if (road.Node is null || road.ForwardNode is null) continue;
            AddIncidentRoad(road.Node.Uid, road);
            AddIncidentRoad(road.ForwardNode.Uid, road);
            AddLink((road.Node.Uid, road.ForwardNode.Uid), road);
            AddLink((road.ForwardNode.Uid, road.Node.Uid), road);
            boundaryNodes.Add(road.Node.Uid);
            boundaryNodes.Add(road.ForwardNode.Uid);
        }
        foreach (var group in (junctions ?? []).GroupBy(p => (p.StartNodeUid, p.EndNodeUid)))
        {
            boundaryNodes.Add(group.Key.StartNodeUid);
            boundaryNodes.Add(group.Key.EndNodeUid);
            if (group.Count() != 1 || links.ContainsKey(group.Key)) ambiguous.Add(group.Key);
            else junctionLinks[group.Key] = group.First();
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


    /// <summary>Nearby directed geometry, preserving the PPD's explicit junction connections.</summary>
    public IReadOnlyList<DirectedRoad> GetNavigationGraph()
        => navigationGraph ??= roads.Where(r => r.Node is not null && r.ForwardNode is not null
                && !ambiguous.Contains((r.Node.Uid, r.ForwardNode.Uid)))
            .SelectMany(r => new[] { Orient(r, r.Node.Uid), Orient(r, r.ForwardNode.Uid) })
            .Concat(junctionLinks.Values).ToArray();
    /// <summary>
    /// Road distance is available only within one continuous route chunk. Across an unknown
    /// prefab, fallback is allowed only when both positions independently match a unique
    /// directed road on the ordered game route and the camera chunk is strictly later.
    /// </summary>
    public CameraCandidate MatchCamera(Vector3 position, Vector3 heading, MonitoredCamera camera,
        RouteSnapshot route, RouteAvailability availability)
    {
        var direct = Vector3.Distance(position, camera.MonitoringPosition);
        CameraCandidate Unknown(string reason = "位置或方向投影不确定") => new(camera.Id, CameraApproach.Unknown, direct) { MatchFailure = reason };
        if (availability == RouteAvailability.None)
        {
            var path = CurrentRoad(position, heading);
            return IsPositionOnPath(camera.MonitoringPosition, path)
                ? matcher.Match(position, heading, camera, path, false)
                : Unknown("无导航：监测点不在当前唯一必经连接链，可能存在分岔");
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
        if (playerChunks.Length != 1) return Unknown($"车辆未唯一匹配导航道路或路口（匹配 {playerChunks.Length} 段）");
        if (cameraChunks.Length != 1) return Unknown($"监测点未唯一匹配导航方向（匹配 {cameraChunks.Length} 段）");
        var player = playerChunks[0];
        var monitor = cameraChunks[0];
        if (player.Index == monitor.Index)
            return matcher.Match(position, heading, camera, player.Path, true);
        if (monitor.Index < player.Index) return new(camera.Id, CameraApproach.Behind, direct);
        // The route can visit a physically nearby camera only after a long detour.
        // Unknown gaps do not erase the length of all the already known road pieces.
        double lowerBound = Math.Max(0, player.Path.Sum(PolylineLength)
            - matcher.ProjectDistance(position, heading, player.Path)!.Value);
        for (int i = player.Index + 1; i <= monitor.Index; i++)
        {
            lowerBound += Vector3.Distance(chunks[i - 1][^1].Points[^1], chunks[i][0].Points[0]);
            lowerBound += i == monitor.Index
                ? matcher.ProjectDistance(camera.MonitoringPosition, camera.TravelDirection, monitor.Path)!.Value
                : chunks[i].Sum(PolylineLength);
        }
        return new(camera.Id, CameraApproach.RouteAhead, direct)
        { RouteDistanceLowerBoundMeters = lowerBound };
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
            DirectedRoad? directed = null;
            if (!ambiguous.Contains(key))
            {
                if (links.TryGetValue(key, out var road) && Near(road, position)) directed = Orient(road, key.Item1);
                else if (junctionLinks.TryGetValue(key, out var junction)) directed = junction;
                else
                {
                    // Native routes may contain internal junction nodes between map control nodes.
                    // Skip only unrecognized internal UIDs, never another known road boundary.
                    for (int next = i + 1; next < Math.Min(nodes.Length, i + 16); next++)
                    {
                        if (boundaryNodes.Contains(nodes[next - 1].Uid)) break;
                        var span = (nodes[i - 1].Uid, nodes[next].Uid);
                        if (junctionLinks.TryGetValue(span, out junction) && !ambiguous.Contains(span))
                        { directed = junction; i = next; break; }
                    }
                }
            }
            if (directed is null) { chunk = null; continue; }
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
        var nearby = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Score: Distance(position, [Orient(r, r.Node.Uid)])))
            .Where(p => p.Score < 12).OrderBy(p => p.Score).ToArray();
        // A nearby clamped road endpoint is not ownership evidence inside a prefab.
        // Apply the same finite-path projection rule used when selecting the current road.
        var candidates = nearby.Where(p => HasProjection(position, Orient(p.Road, p.Road.Node.Uid)))
            .Take(3).ToArray();
        bool endpointsOnly = candidates.Length == 0;
        if (endpointsOnly)
        {
            if (path.Where(p => p.Id.StartsWith("prefab:", StringComparison.Ordinal))
                .Any(p => HasProjection(position, p))) return true;
            // The outside wedge at an ordinary two-road bend can have no interior
            // projection onto either road separately. Preserve its topological proof.
            candidates = nearby.Take(3).ToArray();
            if (candidates.Length < 2) return false;
        }
        if (endpointsOnly || candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3)
        {
            if (candidates.Length > 2 && candidates[2].Score - candidates[0].Score < 3
                || !TryContinuousJoin(position, candidates[0].Road, candidates[1].Road, out var join))
                return false;
            var first = candidates[0].Road.Uid.ToString("X16");
            var second = candidates[1].Road.Uid.ToString("X16");
            return path.Zip(path.Skip(1)).Any(pair =>
                pair.First.EndNodeUid == join && pair.Second.StartNodeUid == join
                && (pair.First.Id == first && pair.Second.Id == second
                    || pair.First.Id == second && pair.Second.Id == first));
        }
        var selectedId = candidates[0].Road.Uid.ToString("X16");
        return path.Any(road => StringComparer.Ordinal.Equals(road.Id, selectedId));
    }

    private bool HasProjection(Vector3 position, DirectedRoad road)
    {
        var nearest = road.Points.Zip(road.Points.Skip(1))
            .Where(p => Vector3.DistanceSquared(p.First, p.Second) > .000001f)
            .OrderBy(p => SegmentDistance(position, p.First, p.Second)).FirstOrDefault();
        var tangent = nearest.Second - nearest.First;
        return matcher.ProjectDistance(position, tangent, [road]).HasValue
            || matcher.ProjectDistance(position, -tangent, [road]).HasValue;
    }

    private IReadOnlyList<DirectedRoad> CurrentRoad(Vector3 position, Vector3 heading)
    {
        var candidates = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Sample: Orient(r, r.Node.Uid)))
            // Clamped distance to a road endpoint is not proof that the vehicle has
            // entered that road: it may still be on the junction immediately before it.
            .Where(p => matcher.ProjectDistance(position, heading, [p.Sample]).HasValue
                || matcher.ProjectDistance(position, -heading, [p.Sample]).HasValue)
            .Select(p => (p.Road, p.Sample, Score: Distance(position, [p.Sample])))
            .Where(p => p.Score < 12).OrderBy(p => p.Score).Take(3).ToArray();
        if (candidates.Length == 0)
        {
            var junctions = junctionLinks.Values.Where(p => !ambiguous.Contains((p.StartNodeUid, p.EndNodeUid))
                && matcher.ProjectDistance(position, heading, [p]).HasValue)
                .Select(p => (Path: p, Error: Distance(position, [p]))).Where(p => p.Error < 12).ToArray();
            // Inside a junction, heading/projection may narrow the legal exits, but
            // several remaining exits are still a real choice without navigation.
            if (junctions.Length == 0 || junctions.Select(p => p.Path.EndNodeUid).Distinct().Count() != 1) return [];
            return Extend(junctions.MinBy(p => p.Error).Path, position);
        }
        var candidate = candidates[0];
        if (candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3)
        {
            if (candidates.Length > 2 && candidates[2].Score - candidates[0].Score < 3
                || !TryContinuousJoin(position, candidates[0].Road, candidates[1].Road, out var join)) return [];
            // Start with the incoming road so the unique continuation includes both
            // sides of this join. A U-turn or conflicting tangent is not certainty.
            var incoming = candidates.Take(2).Where(p =>
            {
                var atJoin = p.Road.Node.Uid == join ? p.Sample.Points[0] : p.Sample.Points[^1];
                var adjacent = p.Road.Node.Uid == join ? p.Sample.Points[1] : p.Sample.Points[^2];
                return Vector3.Dot(Vector3.Normalize(atJoin - adjacent), heading) >= .5f;
            }).ToArray();
            var outgoing = candidates.Take(2).Where(p =>
            {
                var atJoin = p.Road.Node.Uid == join ? p.Sample.Points[0] : p.Sample.Points[^1];
                var adjacent = p.Road.Node.Uid == join ? p.Sample.Points[1] : p.Sample.Points[^2];
                return Vector3.Dot(Vector3.Normalize(adjacent - atJoin), heading) >= .5f;
            }).ToArray();
            if (incoming.Length != 1 || outgoing.Length != 1 || incoming[0].Road.Uid == outgoing[0].Road.Uid) return [];
            candidate = incoming[0];
        }
        var nearest = candidate.Sample.Points.Zip(candidate.Sample.Points.Skip(1))
            .OrderBy(p => SegmentDistance(position, p.First, p.Second)).First();
        var tangent = Vector3.Normalize(nearest.Second - nearest.First);
        var start = Vector3.Dot(tangent, heading) >= 0 ? candidate.Road.Node.Uid : candidate.Road.ForwardNode.Uid;
        return Extend(Orient(candidate.Road, start), position);
    }

    private IReadOnlyList<DirectedRoad> Extend(DirectedRoad current, Vector3 position)
    {
        var path = new List<DirectedRoad> { current };
        var visited = new HashSet<ulong>();
        var visitedJunctions = new HashSet<string>(StringComparer.Ordinal);
        if (current.Id.StartsWith("prefab:", StringComparison.Ordinal)) visitedJunctions.Add(current.Id.Split(':')[1]);
        else if (ulong.TryParse(current.Id, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var uid)) visited.Add(uid);
        double coveredAhead = DistanceToEnd(position, current);
        while (coveredAhead < 1000 && path.Count < 128)
        {
            incidentRoads.TryGetValue(current.EndNodeUid, out var connected);
            var successors = (connected ?? []).Where(r => r.Uid.ToString("X16") != current.Id).ToArray();
            if (successors.Length == 0)
            {
                var exits = junctionLinks.Values.Where(p => p.StartNodeUid == current.EndNodeUid
                    && !ambiguous.Contains((p.StartNodeUid, p.EndNodeUid))
                    && !visitedJunctions.Contains(p.Id.Split(':')[1])).ToArray();
                if (exits.Length != 1) break;
                var junction = exits[0];
                if (Vector3.Distance(current.Points[^1], junction.Points[0]) > 2) break;
                visitedJunctions.Add(junction.Id.Split(':')[1]);
                path.Add(junction);
                coveredAhead += PolylineLength(junction);
                current = junction;
                continue;
            }
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

    private bool TryContinuousJoin(Vector3 position, Road first, Road second, out ulong join)
    {
        join = 0;
        var shared = new[] { first.Node, first.ForwardNode }
            .Where(node => node.Uid == second.Node.Uid || node.Uid == second.ForwardNode.Uid).ToArray();
        if (shared.Length != 1 || Vector3.Distance(position, shared[0].Position) > 12
            || !incidentRoads.TryGetValue(shared[0].Uid, out var connected) || connected.Count != 2)
            return false;
        join = shared[0].Uid;
        return true;
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
