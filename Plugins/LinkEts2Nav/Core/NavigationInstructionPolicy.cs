namespace LinkEts2Nav.Core;

/// <summary>Fixed navigation phrases: distances describe stages, never an unmeasured actual distance.</summary>
public static class NavigationPrompts
{
    public static string Action(ManeuverKind kind) => kind switch
    {
        ManeuverKind.TurnLeft => "左转",
        ManeuverKind.TurnRight => "右转",
        ManeuverKind.KeepLeft => "靠左行驶",
        ManeuverKind.KeepRight => "靠右行驶",
        ManeuverKind.Straight => "直行",
        ManeuverKind.UTurn => "掉头",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Advance(ManeuverKind kind) => "前方三百米，" + Action(kind) + "。";
    public static string Near(ManeuverKind kind) => "前方五十米，" + Action(kind) + "。";
    public static string Generic(ManeuverKind kind) => "前方" + Action(kind) + "。";

    public static IEnumerable<string> AllTexts()
    {
        foreach (var kind in Enum.GetValues<ManeuverKind>())
        {
            yield return Advance(kind);
            yield return Near(kind);
            yield return Generic(kind);
        }
    }
}

/// <summary>
/// Own on the serialized navigation loop. Planning registers a fresh encounter without consuming
/// its stages. Missing geometry, pause or stale data never erase already consumed instructions.
/// </summary>
public sealed class NavigationInstructionPolicy
{
    private const int MaximumEncounters = 2048;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private sealed class Encounter(double firstDistance, DateTimeOffset observedAt, DateTimeOffset now)
    {
        public double FirstDistance { get; } = firstDistance;
        public double MinimumDistance { get; set; } = firstDistance;
        public int Consumed { get; set; }
        public DateTimeOffset ObservedAt { get; set; } = observedAt;
        public DateTimeOffset LastSeen { get; set; } = now;
    }
    private readonly Dictionary<string, Encounter> encounters = new(StringComparer.Ordinal);
    private string? session;

    public IReadOnlyList<string> Plan(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        var encounter = Observe(maneuver, sessionId, observedAt, now, active);
        if (encounter is null || maneuver is null) return [];
        var result = new List<string>(2);
        // When both stages were crossed before confirmation, only the near stage remains.
        if (maneuver.DistanceMeters > 50 && (encounter.Consumed & 1) == 0)
            result.Add(FarText(maneuver.Kind, encounter, maneuver.DistanceMeters));
        if ((encounter.Consumed & 2) == 0)
            result.Add(NearText(maneuver.Kind, encounter, maneuver.DistanceMeters));
        if (result.Count > 0 && !result.Contains(NavigationPrompts.Generic(maneuver.Kind), StringComparer.Ordinal))
            result.Add(NavigationPrompts.Generic(maneuver.Kind));
        return result;
    }

    public IReadOnlyList<string> Update(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        var encounter = Observe(maneuver, sessionId, observedAt, now, active);
        if (encounter is null || maneuver is null || maneuver.DistanceMeters > 300) return [];
        if (maneuver.DistanceMeters <= 50)
        {
            if ((encounter.Consumed & 2) != 0) return [];
            encounter.Consumed = 3;
            return [NearText(maneuver.Kind, encounter, maneuver.DistanceMeters)];
        }
        if ((encounter.Consumed & 1) != 0) return [];
        encounter.Consumed |= 1;
        return [FarText(maneuver.Kind, encounter, maneuver.DistanceMeters)];
    }

    private Encounter? Observe(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        var age = now - observedAt;
        if (!active || age < TimeSpan.Zero || age > MaximumAge || string.IsNullOrWhiteSpace(sessionId)
            || maneuver is null || string.IsNullOrWhiteSpace(maneuver.Id)
            || !Enum.IsDefined(maneuver.Kind) || !double.IsFinite(maneuver.DistanceMeters)
            || maneuver.DistanceMeters < 0) return null;
        if (!StringComparer.Ordinal.Equals(session, sessionId))
        {
            Reset();
            session = sessionId;
        }
        if (encounters.TryGetValue(maneuver.Id, out var existing))
        {
            if (observedAt < existing.ObservedAt) return null;
            // A fresh distant approach after reaching the junction is a new encounter.
            // Merely crossing the 50/300 m thresholds cannot rearm an instruction.
            if (existing.MinimumDistance <= 50 && maneuver.DistanceMeters > 350)
                encounters[maneuver.Id] = existing = new(maneuver.DistanceMeters, observedAt, now);
            existing.MinimumDistance = Math.Min(existing.MinimumDistance, maneuver.DistanceMeters);
            existing.ObservedAt = observedAt;
            existing.LastSeen = now;
            return existing;
        }
        if (encounters.Count >= MaximumEncounters)
        {
            // Bound session memory, retaining the most recently observed intersections.
            string oldest = encounters.OrderBy(p => p.Value.LastSeen)
                .ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
            encounters.Remove(oldest);
        }
        var created = new Encounter(maneuver.DistanceMeters, observedAt, now);
        encounters.Add(maneuver.Id, created);
        return created;
    }

    private static string FarText(ManeuverKind kind, Encounter encounter, double distance)
        => encounter.FirstDistance <= 270 || distance <= 270 ? NavigationPrompts.Generic(kind) : NavigationPrompts.Advance(kind);
    private static string NearText(ManeuverKind kind, Encounter encounter, double distance)
        => encounter.FirstDistance < 40 || distance < 40 ? NavigationPrompts.Generic(kind) : NavigationPrompts.Near(kind);

    public void Reset()
    {
        encounters.Clear();
        session = null;
    }
}
