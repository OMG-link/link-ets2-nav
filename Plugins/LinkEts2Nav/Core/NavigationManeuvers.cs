using System.Numerics;

namespace LinkEts2Nav.Core;

public enum ManeuverKind { TurnLeft, TurnRight, KeepLeft, KeepRight, Straight, UTurn, MergeLeft, MergeRight, EnterRoundabout, ExitRoundabout, PassRoundaboutExit }

public enum NavigationAnnouncementMode { Normal, Silent, NoticeAt500 }

public sealed record NavigationManeuver(string Id, ManeuverKind Kind, double DistanceMeters,
    string EntryId, string ExitId)
{
    /// <summary>Multiple connection curves inside one prefab share this physical-junction group.</summary>
    public string? JunctionId { get; init; }
    public NavigationAnnouncementMode AnnouncementMode { get; init; }
    /// <summary>Optional action-specific wording; distance scheduling remains shared.</summary>
    public string? Instruction { get; init; }
    public string? NearInstruction { get; init; }
    public bool ExecutionOnly { get; init; }
    /// <summary>False when an adjacent prefab, rather than an explicit approach road, precedes this decision.</summary>
    public bool HasSeparateApproachRoad { get; init; } = true;
    /// <summary>True when a straight decision explicitly continues the main road.</summary>
    public bool IsMainRoadContinuation { get; init; }
}

/// <summary>
/// Derives instructions from a continuous directed route and its legal outgoing connections.
/// Own one detector on a serialized loop. Graph snapshots must be immutable; their lookup
/// index is cached by reference. Geometry cannot identify lanes or a roundabout exit number.
/// </summary>
public sealed class NavigationManeuverDetector
{
    private const double TurnAngleDegrees = 45;
    private const double ForkAngleDegrees = 55;
    private const double MinimumForkSpreadDegrees = 5;
    private const double ExitOrderingToleranceDegrees = .5;
    private IReadOnlyList<DirectedRoad>? indexedGraph;
    private Dictionary<ulong, DirectedRoad[]> roadStarts = new();
    private Dictionary<(string Prefab, ulong Start), DirectedRoad[][]> prefabStarts = new();
    private Dictionary<(string Prefab, ulong End), DirectedRoad[]> prefabEnds = new();

    public NavigationManeuver? FindNext(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph) => FindSequence(route, vehicleAlong, graph).FirstOrDefault();

    /// <summary>
    /// Known decisions in route order. An unsupported nearer choice truncates the
    /// sequence; already confirmed earlier decisions remain available to callers.
    /// </summary>
    public IReadOnlyList<NavigationManeuver> FindSequence(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph) => FindSequence(route, vehicleAlong, graph, out _);

    /// <summary>
    /// Also reports the absolute route arc length verified before an unknown junction.
    /// Invalid or disconnected input confirms no distance.
    /// </summary>
    public IReadOnlyList<NavigationManeuver> FindSequence(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph, out double confirmedUntil)
        => FindSequence(route, vehicleAlong, graph, out confirmedUntil, out _);

    public IReadOnlyList<NavigationManeuver> FindSequence(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph, out double confirmedUntil, out bool hasUnresolvedJoin)
        => FindSequence(route, vehicleAlong, graph, out confirmedUntil, out hasUnresolvedJoin, out _);

    /// <summary>Unresolved join position is an absolute route distance, or infinity when none is ahead.</summary>
    public IReadOnlyList<NavigationManeuver> FindSequence(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph, out double confirmedUntil, out bool hasUnresolvedJoin,
        out double firstUnresolvedJoinAlong)
    {
        confirmedUntil = 0;
        hasUnresolvedJoin = false;
        firstUnresolvedJoinAlong = double.PositiveInfinity;
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(graph);
        var result = new List<NavigationManeuver>();
        if (!double.IsFinite(vehicleAlong) || vehicleAlong < 0 || route.Count == 0) return result;
        if (route.Any(r => !Valid(r))) return result;
        for (int i = 1; i < route.Count; i++)
            if (!Connected(route[i - 1], route[i])) return result;
        if (route.Count == 1 && route[0].Roundabout is null)
        {
            if (PrefabKey(route[0]) is null) confirmedUntil = Length(route[0]);
            return result;
        }
        Index(graph);

        double offset = 0;
        for (int i = 0; i < route.Count; i++)
        {
            var current = route[i];
            double end = offset + Length(current);
            if (current.Roundabout is { } ring)
            {
                string id = $"roundabout:{ring.Id}:{current.StartNodeUid:X16}:{current.EndNodeUid:X16}";
                if (offset >= vehicleAlong)
                    result.Add(new(id + ":enter", ManeuverKind.EnterRoundabout, offset - vehicleAlong, current.Id, current.Id)
                    { JunctionId = "roundabout:" + ring.Id, Instruction = "进入环岛", NearInstruction="进入环岛，请从第"+NavigationPrompts.Number(ring.ExitNumber)+"出口驶出" });
                var exits=ring.Exits.Count>0 ? ring.Exits : new[]{new RoundaboutExitCue(ring.ExitNumber,Math.Max(0,end-offset-ring.ExitConnectorMeters))};
                foreach(var exit in exits)
                {
                    double location=offset+exit.AlongMeters;
                    if(location<vehicleAlong)continue;
                    bool selected=exit.Number==ring.ExitNumber;
                    result.Add(new(id+":exit:"+exit.Number,selected?ManeuverKind.ExitRoundabout:ManeuverKind.PassRoundaboutExit,
                        location-vehicleAlong,current.Id,current.Id)
                    {JunctionId="roundabout:"+ring.Id,ExecutionOnly=true,
                        Instruction=selected?"驶出环岛":"经过第"+NavigationPrompts.Number(exit.Number)+"出口"});
                }
                confirmedUntil = end; offset = end; continue;
            }
            // A road is known up to its exit even if the choice beyond it is unknown.
            // A prefab requires its connection to be validated before advancing.
            if (PrefabKey(current) is null) confirmedUntil = end;
            if (PrefabKey(current) is { } prefab && end >= vehicleAlong)
            {
                // Unsupported winding geometry remains a barrier while the vehicle
                // approaches or traverses it. A normal, already entered turn does not.
                if (ComplexInternalBend(current)) return result;
                if (offset < vehicleAlong)
                {
                    if ((prefabEnds.GetValueOrDefault((prefab, current.EndNodeUid)) ?? [])
                        .Any(e => e.StartNodeUid != current.StartNodeUid))
                    {
                        var role = RoadJoinClassifier.Classify(current).Role;
                        if (role is RoadJoinRole.Entering or RoadJoinRole.Ambiguous)
                        { hasUnresolvedJoin = true; firstUnresolvedJoinAlong = Math.Min(firstUnresolvedJoinAlong, offset); }
                    }
                    confirmedUntil = end; offset = end; continue;
                }
                if (!NativeGeometrySupported(current)) return result;
                var connections = prefabStarts.GetValueOrDefault((prefab, current.StartNodeUid)) ?? [];
                if (!connections.Any(g => g[0].EndNodeUid == current.EndNodeUid)) return result;
                if (connections.Length == 1)
                {
                    var merge = MergeDecision(current, prefab);
                    if (merge.Ambiguous)
                    { hasUnresolvedJoin = true; firstUnresolvedJoinAlong = Math.Min(firstUnresolvedJoinAlong, offset); }
                    if (merge.Kind is { } mergeKind)
                        result.Add(new($"merge:{prefab}:{current.StartNodeUid:X16}:{current.EndNodeUid:X16}",
                            mergeKind, offset - vehicleAlong, i > 0 ? route[i - 1].Id : current.Id,
                            i + 1 < route.Count ? route[i + 1].Id : current.Id)
                        { JunctionId = "prefab:" + prefab, HasSeparateApproachRoad = i > 0 && PrefabKey(route[i - 1]) is null });
                }
                // All angle-based maneuvers share this junction-only rule.
                if (connections.Length >= 2 && current.Junction?.IsJunction == true)
                {
                    var previous = i > 0 ? route[i - 1] : null;
                    var following = i + 1 < route.Count ? route[i + 1] : null;
                    var entry = NativeTangent(current, true);
                    if (entry is null) return result;
                    var exits = new List<(ulong End, Vector2 Direction, string? RoadType)>();
                    Vector2? chosen = null;
                    foreach (var group in connections)
                    {
                        var valid = group.Where(Valid).Where(NativeGeometrySupported).ToArray();
                        if (valid.Length == 0) return result;
                        Vector2? direction;
                        string? roadType = null;
                        if (group[0].EndNodeUid == current.EndNodeUid)
                        {
                            direction = NativeTangent(current, false);
                            chosen = direction;
                            roadType = following?.RoadType;
                        }
                        else
                        {
                            var outside = (roadStarts.GetValueOrDefault(group[0].EndNodeUid) ?? [])
                                .Where(e => previous is null || e.Id != previous.Id)
                                .GroupBy(e => (e.Id, e.StartNodeUid, e.EndNodeUid)).Select(g => g.First()).ToArray();
                            if (outside.Length > 1) return result;
                            if (outside.Length == 1)
                            {
                                if (!Valid(outside[0]) || !valid.Any(c => Connected(c, outside[0]))) return result;
                                roadType = outside[0].RoadType;
                            }
                            var headings = valid.Select(c => NativeTangent(c, false)).ToArray();
                            if (headings.Any(h => h is null)) return result;
                            direction = headings[0];
                            if (headings.Any(h => Vector2.Dot(direction!.Value, h!.Value) < .98f)) return result;
                        }
                        if (direction is null) return result;
                        exits.Add((group[0].EndNodeUid, direction.Value, roadType));
                    }
                    if (chosen is null) return result;
                    if (ClassifyJunctionAngles(entry.Value, chosen.Value, exits, previous?.RoadType, current.EndNodeUid, out bool mainContinuation) is { } kind)
                        result.Add(new($"prefab:{prefab}:{current.StartNodeUid:X16}:{current.EndNodeUid:X16}",
                            kind, offset - vehicleAlong, previous?.Id ?? current.Id, following?.Id ?? current.Id)
                        {
                            JunctionId = "prefab:" + prefab,
                            HasSeparateApproachRoad = previous is not null && PrefabKey(previous) is null,
                            IsMainRoadContinuation = mainContinuation
                        });
                }
            }
            else if (PrefabKey(current) is null && i + 1 < route.Count
                && PrefabKey(route[i + 1]) is null && route[i + 1].Roundabout is null && end >= vehicleAlong)
            {
                // Ordinary road joins still validate continuity, but are not junction rules.
                var exits = (roadStarts.GetValueOrDefault(current.EndNodeUid) ?? [])
                    .Where(e => e.Id != current.Id && e.EndNodeUid != current.StartNodeUid).ToArray();
                if (!exits.Any(e => SameEdge(e, route[i + 1]))) return result;
                if (exits.Length >= 2 && exits.Any(e => !Valid(e) || !Connected(current, e))) return result;
            }
            confirmedUntil = end;
            offset = end;
        }
        return result;
    }

    private void Index(IReadOnlyList<DirectedRoad> graph)
    {
        if (ReferenceEquals(indexedGraph, graph)) return;
        var edges = graph.Where(e => e is not null && !string.IsNullOrEmpty(e.Id)).ToArray();
        roadStarts = edges.Where(e => PrefabKey(e) is null).GroupBy(e => e.StartNodeUid)
            .ToDictionary(g => g.Key, g => g.ToArray());
        prefabStarts = edges.Where(e => PrefabKey(e) is not null)
            .GroupBy(e => (Prefab: PrefabKey(e)!, Start: e.StartNodeUid))
            .ToDictionary(g => g.Key, g => g.Where(e => e.EndNodeUid != e.StartNodeUid)
                .GroupBy(e => e.EndNodeUid).Select(x => x.ToArray()).ToArray());
        prefabEnds = edges.Where(e => PrefabKey(e) is not null)
            .GroupBy(e => (Prefab: PrefabKey(e)!, End: e.EndNodeUid))
            .ToDictionary(g => g.Key, g => g.ToArray());
        indexedGraph = graph;
    }

    private (ManeuverKind? Kind, bool Ambiguous) MergeDecision(DirectedRoad selected, string prefab)
    {
        var alternatives = (prefabEnds.GetValueOrDefault((prefab, selected.EndNodeUid)) ?? [])
            .Where(e => e.StartNodeUid != selected.StartNodeUid).ToArray();
        if (alternatives.Length == 0) return (null, false);
        var decision = RoadJoinClassifier.Classify(selected);
        if (decision.Role == RoadJoinRole.Ambiguous) return (null, true);
        if (decision.Role != RoadJoinRole.Entering) return (null, false);
        var main = alternatives.FirstOrDefault(e => e.StartNodeUid == decision.MainEntry);
        if (main is null || main.Points.Count < 4 || selected.Points.Count < 4
            || NativeTangent(main, false) is not { } direction) return (null, true);
        // Geometry only labels left/right after lane structure establishes a road merge.
        var delta = selected.Points[^2] - main.Points[^2];
        double side = direction.X * delta.Z - direction.Y * delta.X;
        if (Math.Abs(side) < .1)
        {
            delta = selected.Points[1] - main.Points[1];
            side = direction.X * delta.Z - direction.Y * delta.X;
        }
        if (Math.Abs(side) < .1) return (null, true);
        return (side > 0 ? ManeuverKind.MergeLeft : ManeuverKind.MergeRight, false);
    }

    private static bool ComplexInternalBend(DirectedRoad prefab)
    {
        Vector2? previous = null;
        double absoluteDegrees = 0;
        for (int i = 1; i < prefab.Points.Count - 2; i++)
        {
            var delta = prefab.Points[i + 1] - prefab.Points[i];
            var horizontal = new Vector2(delta.X, delta.Z);
            if (horizontal.LengthSquared() <= .000001f) continue;
            var direction = Vector2.Normalize(horizontal);
            if (previous is { } before) absoluteDegrees += Math.Abs(Angle(before, direction));
            if (absoluteDegrees > 160) return true;
            previous = direction;
        }
        return false;
    }

    /// <summary>Junction rule: classify all turns, reversals and shallow directions together.
    /// Called only for prefabs confirmed to contain crossing movements.</summary>
    private static ManeuverKind? ClassifyJunctionAngles(Vector2 entry, Vector2 selected,
        IReadOnlyList<(ulong End, Vector2 Direction, string? RoadType)> exits,
        string? entryType, ulong selectedEnd, out bool mainContinuation)
    {
        mainContinuation = false;
        if (exits.Select(e => e.End).Distinct().Count() < 2) return null;
        var choices = exits.Select(e => (e.End, Angle: Angle(entry, e.Direction), e.RoadType)).ToList();
        double chosen = Angle(entry, selected);
        if (Math.Abs(chosen) >= 150) return ManeuverKind.UTurn;
        choices.RemoveAll(c => Math.Abs(c.Angle) >= 150);
        if (choices.Select(c => c.End).Distinct().Count() < 2) return null;
        var angles = choices.Select(c => c.Angle).ToArray();
        if (chosen <= -TurnAngleDegrees) return ManeuverKind.TurnLeft;
        if (chosen >= TurnAngleDegrees) return ManeuverKind.TurnRight;
        if (angles.All(a => Math.Abs(a) <= ForkAngleDegrees) && angles.Max() - angles.Min() >= MinimumForkSpreadDegrees)
        {
            // A unique road-type continuation takes precedence over a nearly straight
            // ramp. Otherwise geometry can still identify an unambiguous straight exit.
            bool typed = !string.IsNullOrWhiteSpace(entryType)
                && choices.All(c => !string.IsNullOrWhiteSpace(c.RoadType));
            ulong? mainEnd = null;
            if (typed)
            {
                var matching = choices.Where(c => c.RoadType == entryType).ToArray();
                if (matching.Length == 1 && Math.Abs(matching[0].Angle) < TurnAngleDegrees
                    && choices.Any(c => c.RoadType != entryType)) mainEnd = matching[0].End;
            }
            if (mainEnd is null)
            {
                var straight = choices.Where(c => Math.Abs(c.Angle) <= 5).ToArray();
                if (straight.Length == 1 && choices.Where(c => c.End != straight[0].End)
                    .All(c => Math.Abs(c.Angle) - Math.Abs(straight[0].Angle) >= 5))
                    mainEnd = straight[0].End;
            }
            if (mainEnd == selectedEnd)
            {
                mainContinuation = true;
                return ManeuverKind.Straight;
            }
            bool left = angles.Any(a => a < chosen - ExitOrderingToleranceDegrees);
            bool right = angles.Any(a => a > chosen + ExitOrderingToleranceDegrees);
            if (!left && right) return ManeuverKind.KeepLeft;
            if (left && !right) return ManeuverKind.KeepRight;
        }
        return ManeuverKind.Straight;
    }

    private static double Angle(Vector2 incoming, Vector2 outgoing)
        => Math.Atan2(incoming.X * outgoing.Y - incoming.Y * outgoing.X,
            Vector2.Dot(incoming, outgoing)) * 180 / Math.PI;

    private static bool NativeGeometrySupported(DirectedRoad edge)
        => edge.Points.Count < 4 || NativeTangent(edge, true).HasValue && NativeTangent(edge, false).HasValue;

    private static Vector2? NativeTangent(DirectedRoad prefab, bool fromStart)
    {
        var native = fromStart ? prefab.NativeEntryTangent : prefab.NativeExitTangent;
        if (native is { } value)
        {
            var horizontal = new Vector2(value.X,value.Z);
            return float.IsFinite(horizontal.X)&&float.IsFinite(horizontal.Y)&&horizontal.LengthSquared()> .000001f
                ? Vector2.Normalize(horizontal) : null;
        }
        // Legacy/synthetic paths can still derive direction from their own geometry.
        // Never sample an adjacent road to infer a prefab's direction.
        if (prefab.Points.Count < 4) return Tangent(prefab,fromStart);
        // First/last points are road-control centers. Interior points are samples
        // of directed PPD curves, and their endpoint tangents describe this junction.
        if (prefab.Points.Count < 4) return null;
        int start = fromStart ? 1 : prefab.Points.Count - 2;
        int end = fromStart ? prefab.Points.Count - 2 : 1;
        int step = fromStart ? 1 : -1;
        for (int i = start; i != end; i += step)
        {
            var delta = fromStart ? prefab.Points[i + step] - prefab.Points[i]
                : prefab.Points[i] - prefab.Points[i + step];
            var horizontal = new Vector2(delta.X, delta.Z);
            if (horizontal.LengthSquared() > .000001f) return Vector2.Normalize(horizontal);
        }
        return null;
    }

    private static Vector2? Tangent(DirectedRoad road, bool fromStart)
    {
        var origin = fromStart ? road.Points[0] : road.Points[^1];
        double length = 0;
        var previous = origin;
        Vector3 target = origin;
        for (int step = 1; step < road.Points.Count; step++)
        {
            target = road.Points[fromStart ? step : road.Points.Count - 1 - step];
            length += Vector3.Distance(previous, target);
            previous = target;
            if (length >= 20) break;
        }
        var delta = fromStart ? target - origin : origin - target;
        var horizontal = new Vector2(delta.X, delta.Z);
        return horizontal.LengthSquared() > .000001f ? Vector2.Normalize(horizontal) : null;
    }

    private static bool SameEdge(DirectedRoad first, DirectedRoad second)
        => first.Id == second.Id && first.StartNodeUid == second.StartNodeUid && first.EndNodeUid == second.EndNodeUid;

    private static bool Connected(DirectedRoad first, DirectedRoad second)
        => first.EndNodeUid == second.StartNodeUid;

    private static string? PrefabKey(DirectedRoad road)
    {
        if (!road.Id.StartsWith("prefab:", StringComparison.Ordinal)) return null;
        var parts = road.Id.Split(':');
        return parts.Length == 4 && parts[1].Length != 0 ? parts[1] : null;
    }

    private static bool Valid(DirectedRoad road)
        => road is not null && !string.IsNullOrWhiteSpace(road.Id)
            && road.StartNodeUid != 0 && road.EndNodeUid != 0 && road.Points is { Count: >= 2 }
            && road.Points.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
            && Length(road) > .001;

    private static double Length(DirectedRoad road)
        => road.Points.Zip(road.Points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));
}
