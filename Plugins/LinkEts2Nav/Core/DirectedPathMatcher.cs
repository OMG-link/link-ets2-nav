using System.Numerics;

namespace LinkEts2Nav.Core;

/// <summary>Native prefab control-interface and crossing counts.</summary>
public sealed record PrefabJunctionInfo(int EntranceCount,int ExitCount,int CrossingCount)
{
    public bool IsJunction => EntranceCount>=2 && ExitCount>=2 && CrossingCount>0;
}

/// <summary>Polyline points and node UIDs are ordered in the permitted direction of travel.</summary>
public sealed record DirectedRoad(
    string Id,
    ulong StartNodeUid,
    ulong EndNodeUid,
    IReadOnlyList<Vector3> Points)
{
    /// <summary>Map road-type token; absent for prefab connections or unavailable metadata.</summary>
    public string? RoadType { get; init; }
    public PrefabJunctionInfo? Junction { get; init; }
    /// <summary>World-space endpoint tangents from native PPD rotations.</summary>
    public Vector3? NativeEntryTangent { get; init; }
    public Vector3? NativeExitTangent { get; init; }
    public IReadOnlyList<PrefabLanePath> LanePaths { get; init; } = [];
    public RoundaboutPassage? Roundabout { get; init; }
    public IReadOnlyList<LaneConnection>? LaneConnections { get; init; }
    /// <summary>Snapshot-local decision for independent entrance groups adding disjoint exit lanes.</summary>
    public RoadJoinPriority? JoinPriority { get; init; }
    public RoadForkInfo? ForkInfo { get; init; }
}

public sealed record RoundaboutPassage(string Id, int ExitNumber, double ExitConnectorMeters = 0)
{
    public IReadOnlyList<RoundaboutExitCue> Exits { get; init; } = [];
}
public sealed record RoundaboutExitCue(int Number,double AlongMeters);

public sealed record LaneConnection(ulong EntryNode, int EntryLane, ulong ExitNode, int ExitLane);

/// <summary>The road monitoring position and permitted traffic direction, not the model's facing.</summary>
public sealed record MonitoredCamera(string Id, Vector3 MonitoringPosition, Vector3 TravelDirection);

/// <summary>
/// Projects a player and camera onto a directed continuous path. For no-route matching,
/// the caller must supply only the continuation proven unavoidable by road topology.
/// Shared interface UIDs establish connectivity; reference-line offsets do not veto it.
/// </summary>
public sealed class DirectedPathMatcher
{
    private readonly double _horizontalTolerance;
    private readonly double _verticalTolerance;
    private readonly double _minimumDirectionDot;

    public DirectedPathMatcher(double horizontalToleranceMeters = 12,
        double verticalToleranceMeters = 3, double minimumDirectionDot = 0.5)
    {
        if (!double.IsFinite(horizontalToleranceMeters) || horizontalToleranceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalToleranceMeters));
        if (!double.IsFinite(verticalToleranceMeters) || verticalToleranceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(verticalToleranceMeters));
        if (!double.IsFinite(minimumDirectionDot) || minimumDirectionDot <= 0 || minimumDirectionDot > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumDirectionDot));
        _horizontalTolerance = horizontalToleranceMeters;
        _verticalTolerance = verticalToleranceMeters;
        _minimumDirectionDot = minimumDirectionDot;
    }

    public CameraCandidate Match(Vector3 vehiclePosition, Vector3 vehicleDirection,
        MonitoredCamera camera, IReadOnlyList<DirectedRoad> orderedForwardRoads,
        bool hasNavigationRoute)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(orderedForwardRoads);
        var directDistance = Vector3.Distance(vehiclePosition, camera.MonitoringPosition);
        CameraCandidate Unknown(string reason) => new(camera.Id, CameraApproach.Unknown, directDistance) { MatchFailure = reason };
        if (!Finite(vehiclePosition) || !Finite(camera.MonitoringPosition)
            || !TryNormalizeHorizontal(vehicleDirection, out var vehicleHeading)
            || !TryNormalizeHorizontal(camera.TravelDirection, out var cameraHeading))
            return Unknown("车辆或监测点位置、方向无效");

        var segments = BuildSegments(orderedForwardRoads);
        if (segments is null)
            return Unknown("必经路径节点未连接或几何无效");
        var player = Project(vehiclePosition, vehicleHeading, segments);
        var monitor = Project(camera.MonitoringPosition, cameraHeading, segments);
        if (player is null) return Unknown("车辆在必经路径上的投影不确定");
        if (monitor is null) return Unknown("监测点在必经路径上的投影不确定");

        var alongDistance = monitor.Value.AlongDistance - player.Value.AlongDistance;
        return new CameraCandidate(camera.Id,
            alongDistance > 0
                ? hasNavigationRoute ? CameraApproach.RouteAhead : CameraApproach.UnavoidableAhead
                : CameraApproach.Behind,
            directDistance, alongDistance);
    }

    /// <summary>Returns an unambiguous forward-oriented arc coordinate on a continuous path.</summary>
    public double? ProjectDistance(Vector3 position, Vector3 direction, IReadOnlyList<DirectedRoad> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Finite(position) || !TryNormalizeHorizontal(direction, out var heading)) return null;
        var segments = BuildSegments(path);
        return segments is null ? null : Project(position, heading, segments)?.AlongDistance;
    }
    private List<Segment>? BuildSegments(IReadOnlyList<DirectedRoad> roads)
    {
        if (roads.Count == 0)
            return null;
        var segments = new List<Segment>();
        double offset = 0;
        DirectedRoad? previous = null;
        foreach (var road in roads)
        {
            if (road.Points.Count < 2 || road.StartNodeUid == 0 || road.EndNodeUid == 0
                || road.Points.Any(point => !Finite(point)))
                return null;
            if (previous is not null && previous.EndNodeUid != road.StartNodeUid)
                return null;
            var countBeforeRoad = segments.Count;
            for (var i = 1; i < road.Points.Count; i++)
            {
                var start = road.Points[i - 1];
                var end = road.Points[i];
                var length = Vector3.Distance(start, end);
                if (!float.IsFinite(length))
                    return null;
                if (length < 0.001f)
                    continue;
                if (!TryNormalizeHorizontal(end - start, out var heading))
                    return null;
                segments.Add(new Segment(start, end, heading, offset, length));
                offset += length;
            }
            if (segments.Count == countBeforeRoad)
                return null;
            previous = road;
        }
        return segments;
    }

    private Projection? Project(Vector3 position, Vector2 heading, List<Segment> segments)
    {
        var candidates = new List<Projection>();
        for (int index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (Vector2.Dot(heading, segment.Heading) < _minimumDirectionDot)
                continue;
            var start = new Vector2(segment.Start.X, segment.Start.Z);
            var end = new Vector2(segment.End.X, segment.End.Z);
            var location = new Vector2(position.X, position.Z);
            var delta = end - start;
            var t = Vector2.Dot(location - start, delta) / delta.LengthSquared();
            // Fill only the outside wedge at an internal bend: neither adjacent
            // segment has an interior perpendicular projection there. Never borrow
            // a later segment endpoint to extend the overall path's start or end.
            if (t < 0 && (index == 0 || Parameter(location, segments[index - 1]) < 1))
                continue;
            if (t > 1 && (index == segments.Count - 1 || Parameter(location, segments[index + 1]) > 0))
                continue;
            t = Math.Clamp(t, 0, 1);
            var projected = Vector3.Lerp(segment.Start, segment.End, t);
            var horizontalError = Vector2.Distance(location, start + delta * t);
            var verticalError = Math.Abs(position.Y - projected.Y);
            if (horizontalError > _horizontalTolerance || verticalError > _verticalTolerance)
                continue;
            candidates.Add(new Projection(segment.Offset + segment.Length * t,
                Math.Sqrt(horizontalError * horizontalError + verticalError * verticalError)));
        }
        if (candidates.Count == 0)
            return null;
        var best = candidates.MinBy(projection => projection.Error);
        // Nearby segments at a normal bend/join represent the same projection. A second
        // similarly close location far along the path indicates a loop, parallel return
        // or overlapping level; choosing a path occurrence would be a guess.
        if (candidates.Any(candidate => candidate.Error <= best.Error + 2
            && Math.Abs(candidate.AlongDistance - best.AlongDistance) > _horizontalTolerance * 2 + 1))
            return null;
        return best;
    }

    private static float Parameter(Vector2 location, Segment segment)
    {
        var start = new Vector2(segment.Start.X, segment.Start.Z);
        var delta = new Vector2(segment.End.X, segment.End.Z) - start;
        return Vector2.Dot(location - start, delta) / delta.LengthSquared();
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool TryNormalizeHorizontal(Vector3 vector, out Vector2 normalized)
    {
        normalized = default;
        if (!Finite(vector))
            return false;
        var horizontal = new Vector2(vector.X, vector.Z);
        if (!float.IsFinite(horizontal.LengthSquared()) || horizontal.LengthSquared() < 0.000001f)
            return false;
        normalized = Vector2.Normalize(horizontal);
        return true;
    }

    private readonly record struct Segment(Vector3 Start, Vector3 End, Vector2 Heading, double Offset, double Length);
    private readonly record struct Projection(double AlongDistance, double Error);
}
