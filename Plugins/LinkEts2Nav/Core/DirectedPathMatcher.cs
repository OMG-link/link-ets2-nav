using System.Numerics;
using System.Runtime.CompilerServices;

namespace LinkEts2Nav.Core;

/// <summary>Native prefab control-interface and crossing counts.</summary>
public sealed record PrefabJunctionInfo(int EntranceCount,int ExitCount,int CrossingCount)
{
    public bool IsJunction => EntranceCount>=2 && ExitCount>=2 && CrossingCount>0;
    public int InterfaceCount { get; init; }
    /// <summary>Native control-interface widths, measured between the outermost input/output lane centers.</summary>
    public IReadOnlyList<double> InterfaceWidths { get; init; }=[];
    public bool IsRampJunction => !IsJunction && CrossingCount==0 && InterfaceCount>=3
        && ExitCount>=2;
    /// <summary>Opposite-direction exits do not create a choice for this approach.</summary>
    public bool IsRampPassage(int reachableExitInterfaces)=>IsRampJunction&&reachableExitInterfaces>=2;
    public bool IsAnyJunction => IsJunction || IsRampJunction;
    public bool IsMinorPassage(int entry,int exit)
        =>entry!=exit&&entry>=0&&exit>=0&&entry<InterfaceWidths.Count&&exit<InterfaceWidths.Count
            &&InterfaceWidths[entry]>=3&&InterfaceWidths[exit]>=3
            &&InterfaceWidths.Count(w=>w>=3)==2;
}

/// <summary>Polyline points and node UIDs are ordered in the permitted direction of travel.</summary>
public sealed record DirectedRoad(
    string Id,
    ulong StartNodeUid,
    ulong EndNodeUid,
    IReadOnlyList<Vector3> Points)
{
    /// <summary>A native prefab passage returning to its own road interface, selected explicitly by route UIDs.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReturnsToEntry=>StartNodeUid==EndNodeUid&&Id.StartsWith("prefab:",StringComparison.Ordinal);
    /// <summary>Indices starting a separate native edge; the preceding gap has no route mileage.</summary>
    public IReadOnlyList<int> SegmentStarts { get; init; } = [];
    /// <summary>Map interface centers for topology queries. A PPD lane may start or end well away from them.</summary>
    internal (Vector3 Entry,Vector3 Exit)? ControlPositions { get; init; }
    internal Vector3 EntryInterface => ControlPositions?.Entry ?? Points[0];
    internal Vector3 ExitInterface => ControlPositions?.Exit ?? Points[^1];
    /// <summary>Map road-type token; absent for prefab connections or unavailable metadata.</summary>
    public string? RoadType { get; init; }
    /// <summary>Loaded road-reference half-width; absent for prefabs and unresolved road definitions.</summary>
    public double? ReferenceHalfWidthMeters { get; init; }
    public PrefabJunctionInfo? Junction { get; init; }
    /// <summary>Only the chosen entrance and exit are interfaces at least three meters wide.</summary>
    public bool IsMinorStraight { get; init; }
    /// <summary>World-space endpoint tangents from native PPD rotations.</summary>
    public Vector3? NativeEntryTangent { get; init; }
    public Vector3? NativeExitTangent { get; init; }
    public IReadOnlyList<PrefabLanePath> LanePaths { get; init; } = [];
    internal PrefabLanePath? ObservedLane { get; init; }
    // Native alternatives on this exact passage. Null stations retain a competing physical
    // lane for ambiguity checks when its mileage cannot be expressed on the chosen route.
    internal IReadOnlyList<(PrefabLanePath Lane, double[]? Stations)> ProjectionLanes { get; init; } = [];
    /// <summary>Native curve starts in this exact representative polyline.</summary>
    public IReadOnlyList<PrefabCurveSpan> NativeCurveSpans { get; init; } = [];
    public IReadOnlyList<LaneConnection>? LaneConnections { get; init; }
    /// <summary>Snapshot-local decision for independent entrance groups adding disjoint exit lanes.</summary>
    public RoadJoinPriority? JoinPriority { get; init; }
    /// <summary>Snapshot-local continuity priority of competing outlets, measured in reverse travel.</summary>
    public RoadJoinPriority? SplitPriority { get; init; }
    public LinkEts2Nav.Map.MissingMapObjects? JoinPriorityDependency { get; init; }
    public LinkEts2Nav.Map.MissingMapObjects? SplitPriorityDependency { get; init; }
    public RoadForkInfo? ForkInfo { get; init; }
}

public sealed record PrefabCurveSpan(int CurveIndex,int PointIndex);

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
    internal const double ProjectionTieMeters=2;
    private readonly double _horizontalTolerance;
    private readonly double _verticalTolerance;
    private readonly double _minimumDirectionDot;
    private static readonly ConditionalWeakTable<IReadOnlyList<DirectedRoad>, PreparedRoute> preparedRoutes=new();

    // Map snapshots publish complete components and reuse them across navigation,
    // cameras and speed limits. An ad-hoc caller's geometry remains uncached.
    internal static void RegisterComponent(IReadOnlyList<DirectedRoad> component)
        =>preparedRoutes.GetValue(component,static path=>new(path.ToArray()));

    private sealed class PreparedRoute(DirectedRoad[] edges)
    {
        internal readonly Lazy<List<Segment>?> Segments=new(()=>BuildSegments(edges));
        internal bool Matches(IReadOnlyList<DirectedRoad> path)
        {
            if(path.Count!=edges.Length)return false;
            for(int i=0;i<edges.Length;i++)if(!ReferenceEquals(path[i],edges[i]))return false;
            return true;
        }
    }

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

        var segments = SegmentsOf(orderedForwardRoads);
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
    public double? ProjectDistance(Vector3 position, Vector3 direction, IReadOnlyList<DirectedRoad> path,int firstVisitIndex=0)
        =>ProjectDistance(position,direction,path,firstVisitIndex,null);

    internal double? ProjectDistance(Vector3 position,Vector3 direction,IReadOnlyList<DirectedRoad> path,int firstVisitIndex,int? requiredVisit)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Finite(position) || !TryNormalizeHorizontal(direction, out var heading)) return null;
        var segments = SegmentsOf(path);
        return segments is null ? null : Project(position, heading, segments,firstVisitIndex,requiredVisit)?.AlongDistance;
    }

    internal bool Near(Vector3 position,Vector3 minimum,Vector3 maximum)
        =>position.X>=minimum.X-_horizontalTolerance && position.X<=maximum.X+_horizontalTolerance
            &&position.Z>=minimum.Z-_horizontalTolerance && position.Z<=maximum.Z+_horizontalTolerance
            &&position.Y>=minimum.Y-_verticalTolerance && position.Y<=maximum.Y+_verticalTolerance;

    /// <summary>Closest directed, same-level projection inside the finite physical polyline.</summary>
    public double? ProjectionError(Vector3 position, Vector3 direction, IReadOnlyList<Vector3> points,
        double? referenceHalfWidthMeters=null)
        =>ProjectPolyline(position,direction,points,referenceHalfWidthMeters)?.Error;

    internal (Vector3 Position,Vector2 Heading,double Error)? ProjectPolyline(Vector3 position, Vector3 direction,
        IReadOnlyList<Vector3> points,double? referenceHalfWidthMeters=null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2 || !Finite(points[0]) || !Finite(position)
            || !TryNormalizeHorizontal(direction, out var heading)) return null;
        var location = new Vector2(position.X,position.Z);
        double tolerance = Tolerance(referenceHalfWidthMeters);
        Line? previous = null, current = null;
        (Vector3 Position,Vector2 Heading,double Error)? best = null;
        void Observe(Line line, Line? next)
        {
            if (TryProject(position, location, heading, line, previous, next, tolerance, out float t, out double error)
                && (best is null || error < best.Value.Error))
                best=(Vector3.Lerp(line.Start,line.End,t),line.Heading,error);
        }
        for(int i=1;i<points.Count;i++)
        {
            var start=points[i-1];var end=points[i];
            float squared=Vector3.DistanceSquared(start,end);
            if(!float.IsFinite(squared))return null;
            if(squared<.000001f)continue;
            if(!TryNormalizeHorizontal(end-start,out var tangent))return null;
            var next=new Line(start,end,tangent);
            if(current is {} line)Observe(line,next);
            previous=current;current=next;
        }
        if(current is {} last)Observe(last,null);
        return best;
    }

    private double Tolerance(double? width) => width is > 0 && double.IsFinite(width.Value)
        ? Math.Max(_horizontalTolerance, width.Value) : _horizontalTolerance;

    private static List<Segment>? SegmentsOf(IReadOnlyList<DirectedRoad> path)
        =>preparedRoutes.TryGetValue(path,out var route)&&route.Matches(path)?route.Segments.Value:BuildSegments(path);

    private static List<Segment>? BuildSegments(IReadOnlyList<DirectedRoad> roads)
    {
        if (roads.Count == 0)
            return null;
        var segments = new List<Segment>();
        double offset = 0;
        DirectedRoad? previous = null;
        int previousSegment = -1;
        for(int visit=0;visit<roads.Count;visit++)
        {
            var road=roads[visit];
            if (road.StartNodeUid == 0 || road.EndNodeUid == 0)
                return null;
            if (previous is not null && previous.EndNodeUid != road.StartNodeUid)
                return null;
            var roadKey=NativeRoadKey.Of(road);
            double width=road.ReferenceHalfWidthMeters is >0 && double.IsFinite(road.ReferenceHalfWidthMeters.Value)
                ?road.ReferenceHalfWidthMeters.Value:0;
            if (!AppendSegments(segments, road.Points, road.SegmentStarts, offset, visit, roadKey,
                width, null, 0, true, ref previousSegment, out double length)) return null;
            foreach (var (lane, stations) in road.ProjectionLanes)
            {
                int lanePrevious = -1;
                if (!AppendSegments(segments, lane.Points, [], offset, visit, roadKey,
                    width, stations, lane.LengthMeters - length, stations is not null, ref lanePrevious, out _)) return null;
            }
            offset += length;
            previous = road;
        }
        return segments;
    }

    private static bool AppendSegments(List<Segment> segments, IReadOnlyList<Vector3> points, IReadOnlyList<int> breaks,
        double offset, int visit, NativeRoadKey road, double width, double[]? stations, double legDifference,
        bool hasMileage, ref int previous, out double length)
    {
        length = 0;
        if (points.Count < 2 || points.Any(p => !Finite(p))) return false;
        int countBefore = segments.Count;
        for (int i = 1; i < points.Count; i++)
        {
            if (breaks.Contains(i)) { previous = -1; continue; }
            var start = points[i - 1];
            var end = points[i];
            float meters = Vector3.Distance(start, end);
            if (!float.IsFinite(meters)) return false;
            bool mapped=stations is not null && double.IsFinite(stations[i-1]) && double.IsFinite(stations[i]);
            double along = offset + (mapped?stations![i-1]:length);
            length += meters;
            if (meters < .001f) continue;
            if (!TryNormalizeHorizontal(end - start, out var heading)) return false;
            int connected = previous >= 0 && segments[previous].Line.End == start ? previous : -1;
            if (connected >= 0) segments[connected] = segments[connected] with { Next = segments.Count };
            previous = segments.Count;
            double span = mapped ? stations![i] - stations[i - 1] : meters;
            segments.Add(new(new(start,end,heading),along,span,width,visit,road,connected,-1,
                stations is null?hasMileage:mapped,legDifference));
        }
        return segments.Count > countBefore;
    }

    private Projection? Project(Vector3 position, Vector2 heading, List<Segment> segments,int firstVisitIndex=0,int? requiredVisit=null)
    {
        var candidates = new List<Projection>();
        var firstQualifiedVisit=new Dictionary<NativeRoadKey,int>();
        var location = new Vector2(position.X, position.Z);
        foreach (var segment in segments)
        {
            if(segment.VisitIndex<firstVisitIndex)continue;
            double tolerance=Math.Max(segment.ReferenceHalfWidth,_horizontalTolerance);
            var line=segment.Line;
            if(position.X<Math.Min(line.Start.X,line.End.X)-tolerance || position.X>Math.Max(line.Start.X,line.End.X)+tolerance
                ||position.Z<Math.Min(line.Start.Z,line.End.Z)-tolerance || position.Z>Math.Max(line.Start.Z,line.End.Z)+tolerance)continue;
            if (!TryProject(position, location, heading, segment.Line,
                segment.Previous<0?null:segments[segment.Previous].Line,
                segment.Next<0?null:segments[segment.Next].Line, tolerance, out float t, out double error)) continue;
            // An ordered route may revisit the same physical directed road.
            // Keep all geometric candidates in its first qualified live visit,
            // but do not let future visits duplicate that physical projection.
            if(firstQualifiedVisit.TryGetValue(segment.Road,out int first)&&first!=segment.VisitIndex)continue;
            firstQualifiedVisit.TryAdd(segment.Road,segment.VisitIndex);
            candidates.Add(new(segment.Offset + segment.Length * t, error,
                tolerance,segment.HasMileage,segment.LegDifference,segment.VisitIndex));
        }
        Projection? nearest=null;
        foreach(var candidate in candidates)
            if(candidate.HasMileage && (nearest is null || candidate.Error < nearest.Value.Error)) nearest=candidate;
        if(nearest is not {} best)return null;
        if(requiredVisit is {} visit && (best.Visit!=visit
            || candidates.Any(c=>c.Visit!=visit && c.Error<=best.Error+ProjectionTieMeters)))return null;
        // Nearby segments at a normal bend/join represent the same projection. A second
        // similarly close location far along a single visit or on a different
        // directed road remains a loop/parallel/level ambiguity.
        if (candidates.Any(candidate => candidate.Error <= best.Error + ProjectionTieMeters && (candidate.HasMileage
            ? Math.Abs(candidate.AlongDistance - best.AlongDistance) > Math.Max(candidate.LateralTolerance,best.LateralTolerance) * 2 + 1
            : Math.Abs(candidate.AlongDistance - best.AlongDistance) > LaneRouteConnections.NativeSeamMeters
                || Math.Abs(candidate.LegDifference) > LaneRouteConnections.NativeSeamMeters)))
            return null;
        return best;
    }

    private bool TryProject(Vector3 position, Vector2 location, Vector2 heading, Line line, Line? previous, Line? next,
        double tolerance, out float t, out double error)
    {
        t=0;error=0;
        if (Vector2.Dot(heading, line.Heading) < _minimumDirectionDot) return false;
        t = Parameter(location, line);
        // The outside wedge belongs only to adjacent segments on a physically connected line.
        if (t < 0 && (previous is not {} before || before.End!=line.Start || Parameter(location, before)<1)
            || t > 1 && (next is not {} after || after.Start!=line.End || Parameter(location, after)>0)) return false;
        t = Math.Clamp(t, 0, 1);
        var projected = Vector3.Lerp(line.Start, line.End, t);
        float horizontal = Vector2.Distance(location, new(projected.X, projected.Z));
        float vertical = Math.Abs(position.Y - projected.Y);
        if (horizontal > tolerance || vertical > _verticalTolerance) return false;
        error=Math.Sqrt(horizontal * horizontal + vertical * vertical);
        return true;
    }

    private static float Parameter(Vector2 location, Line segment)
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

    private readonly record struct Line(Vector3 Start, Vector3 End, Vector2 Heading);
    private readonly record struct Segment(Line Line, double Offset, double Length,
        double ReferenceHalfWidth,int VisitIndex,NativeRoadKey Road,int Previous,int Next,bool HasMileage,double LegDifference);
    private readonly record struct Projection(double AlongDistance, double Error,double LateralTolerance,bool HasMileage,double LegDifference,int Visit);
}
