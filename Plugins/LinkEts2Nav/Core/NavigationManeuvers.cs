using System.Numerics;

namespace LinkEts2Nav.Core;

public enum ManeuverKind { TurnLeft, TurnRight, KeepLeft, KeepRight, Straight, UTurn }

public sealed record NavigationManeuver(string Id, ManeuverKind Kind, double DistanceMeters,
    string EntryId, string ExitId);

/// <summary>
/// Derives instructions from a continuous directed route and its legal outgoing connections.
/// The graph must already exclude prohibited travel directions and vehicle restrictions.
/// Geometry alone cannot identify lanes, a roundabout or an exit number.
/// </summary>
public sealed class NavigationManeuverDetector
{
    private const float MaximumJoinGap = 2;
    private const double TurnAngleDegrees = 45;
    private const double ForkAngleDegrees = 55;

    public NavigationManeuver? FindNext(IReadOnlyList<DirectedRoad> route, double vehicleAlong,
        IReadOnlyList<DirectedRoad> graph)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(graph);
        if (!double.IsFinite(vehicleAlong) || vehicleAlong < 0 || route.Count < 2) return null;
        if (route.Any(r => !Valid(r))) return null;
        for (int i = 1; i < route.Count; i++)
            if (!Connected(route[i - 1], route[i])) return null;

        double offset = 0;
        for (int i = 0; i < route.Count; i++)
        {
            var current = route[i];
            double end = offset + Length(current);
            if (PrefabKey(current) is { } prefab && end >= vehicleAlong)
            {
                // Do not replace a winding junction by its net entry/exit angle,
                // or skip an unknown near junction to announce a later choice.
                if (ComplexInternalBend(current)) return null;
                var connections = graph.Where(e => e is not null && !string.IsNullOrEmpty(e.Id)
                    && PrefabKey(e) == prefab && e.StartNodeUid == current.StartNodeUid
                    && e.EndNodeUid != current.StartNodeUid).GroupBy(e => e.EndNodeUid).ToArray();
                if (!connections.Any(g => g.Key == current.EndNodeUid)) return null;
                if (connections.Length >= 2)
                {
                    if (i == 0 || i + 1 >= route.Count || offset < vehicleAlong
                        || PrefabKey(route[i - 1]) is not null || PrefabKey(route[i + 1]) is not null)
                        return null;
                    var incoming = route[i - 1];
                    var outgoing = route[i + 1];
                    var exits = new List<DirectedRoad>();
                    foreach (var connection in connections)
                    {
                        var validConnections = connection.Where(Valid).ToArray();
                        if (validConnections.Length == 0) return null;
                        if (connection.Key == current.EndNodeUid) { exits.Add(outgoing); continue; }
                        var roads = graph.Where(e => e is not null && !string.IsNullOrEmpty(e.Id)
                            && PrefabKey(e) is null && e.StartNodeUid == connection.Key && e.Id != incoming.Id)
                            .GroupBy(e => (e.Id, e.StartNodeUid, e.EndNodeUid)).Select(g => g.First()).ToArray();
                        if (roads.Length != 1 || !Valid(roads[0])
                            || !validConnections.Any(c => Connected(c, roads[0]))) return null;
                        exits.Add(roads[0]);
                    }
                    if (!KnownDirections(incoming, outgoing, exits)) return null;
                    if (Classify(incoming, outgoing, exits) is { } kind)
                        return new($"prefab:{prefab}:{current.StartNodeUid:X16}:{current.EndNodeUid:X16}",
                            kind, offset - vehicleAlong, incoming.Id, outgoing.Id);
                }
            }
            else if (PrefabKey(current) is null && i + 1 < route.Count
                && PrefabKey(route[i + 1]) is null && end >= vehicleAlong)
            {
                var outgoing = route[i + 1];
                var exits = graph.Where(e => e is not null && !string.IsNullOrEmpty(e.Id)
                    && PrefabKey(e) is null && e.StartNodeUid == current.EndNodeUid && e.Id != current.Id
                    && e.EndNodeUid != current.StartNodeUid)
                    .GroupBy(e => (e.Id, e.StartNodeUid, e.EndNodeUid)).Select(g => g.First()).ToArray();
                if (!exits.Any(e => SameEdge(e, outgoing))) return null;
                if (exits.Length >= 2)
                {
                    if (exits.Any(e => !Valid(e) || !Connected(current, e))
                        || !KnownDirections(current, outgoing, exits)) return null;
                    if (Classify(current, outgoing, exits) is { } kind)
                        return new($"node:{current.EndNodeUid:X16}:{outgoing.EndNodeUid:X16}",
                            kind, end - vehicleAlong, current.Id, outgoing.Id);
                }
            }
            offset = end;
        }
        return null;
    }

    private static bool KnownDirections(DirectedRoad incoming, DirectedRoad selected,
        IReadOnlyList<DirectedRoad> exits)
        => Tangent(incoming, fromStart: false).HasValue && Tangent(selected, fromStart: true).HasValue
            && exits.All(e => Tangent(e, fromStart: true).HasValue);

    private static bool ComplexInternalBend(DirectedRoad prefab)
    {
        Vector2? previous = null;
        double absoluteDegrees = 0;
        // The first and last segments join road reference nodes to lane geometry.
        // Their lateral offsets must not count as actual junction curvature.
        for (int i = 1; i < prefab.Points.Count - 2; i++)
        {
            var delta = prefab.Points[i + 1] - prefab.Points[i];
            var horizontal = new Vector2(delta.X, delta.Z);
            if (horizontal.LengthSquared() <= .000001f) continue;
            var direction = Vector2.Normalize(horizontal);
            if (previous is { } before)
                absoluteDegrees += Math.Abs(Angle(before, direction));
            if (absoluteDegrees > 160) return true;
            previous = direction;
        }
        return false;
    }

    private static ManeuverKind? Classify(DirectedRoad incoming, DirectedRoad selected,
        IReadOnlyList<DirectedRoad> exits)
    {
        // A forced bend is not a choice and must not become a turn instruction.
        if (exits.Select(e => e.EndNodeUid).Distinct().Count() < 2) return null;
        if (Tangent(incoming, fromStart: false) is not { } entry
            || Tangent(selected, fromStart: true) is not { } exit) return null;
        var choices = new List<(ulong End, double Angle)>();
        foreach (var road in exits)
        {
            if (Tangent(road, fromStart: true) is not { } tangent) return null;
            choices.Add((road.EndNodeUid, Angle(entry, tangent)));
        }
        double chosen = Angle(entry, exit);
        if (Math.Abs(chosen) >= 150) return ManeuverKind.UTurn;
        // A different edge that merely returns the driver to the approach is not
        // another forward choice, even when it has a distinct map item UID.
        choices.RemoveAll(c => Math.Abs(c.Angle) >= 150);
        if (choices.Select(c => c.End).Distinct().Count() < 2) return null;
        var angles = choices.Select(c => c.Angle).ToArray();
        if (chosen <= -TurnAngleDegrees) return ManeuverKind.TurnLeft;
        if (chosen >= TurnAngleDegrees) return ManeuverKind.TurnRight;
        // A shallow Y is a keep instruction; a perpendicular side street does not
        // make the through road a "keep left/right" fork.
        if (angles.All(a => Math.Abs(a) <= ForkAngleDegrees) && angles.Max() - angles.Min() >= 10)
        {
            bool left = angles.Any(a => a < chosen - 5);
            bool right = angles.Any(a => a > chosen + 5);
            if (!left && right) return ManeuverKind.KeepLeft;
            if (left && !right) return ManeuverKind.KeepRight;
        }
        return ManeuverKind.Straight;
    }

    private static double Angle(Vector2 incoming, Vector2 outgoing)
        => Math.Atan2(incoming.X * outgoing.Y - incoming.Y * outgoing.X,
            Vector2.Dot(incoming, outgoing)) * 180 / Math.PI;

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
        => first.EndNodeUid == second.StartNodeUid
            && Vector3.Distance(first.Points[^1], second.Points[0]) <= MaximumJoinGap;

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
