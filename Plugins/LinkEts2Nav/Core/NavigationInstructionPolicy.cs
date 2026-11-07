namespace LinkEts2Nav.Core;

/// <summary>Fixed phrases for 50 m distance steps and an immediate maneuver cue.</summary>
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

    public static string AtDistance(ManeuverKind kind, int meters)
    {
        string number = meters switch
        {
            50 => "五十", 100 => "一百", 150 => "一百五十", 200 => "二百", 250 => "二百五十",
            300 => "三百", 350 => "三百五十", 400 => "四百", 450 => "四百五十", 500 => "五百",
            _ => throw new ArgumentOutOfRangeException(nameof(meters), "Use 50 m steps from 50 to 500.")
        };
        return "前方" + number + "米，" + Action(kind) + "。";
    }

    public static string Immediate(ManeuverKind kind)
        => kind == ManeuverKind.Straight ? "保持直行。" : "即将" + Action(kind) + "。";

    public static string Advance(ManeuverKind kind) => AtDistance(kind, 500);
    public static string Near(ManeuverKind kind) => AtDistance(kind, 50);
    public static string Generic(ManeuverKind kind) => Immediate(kind);

    public static IEnumerable<string> AllTexts()
    {
        foreach (var kind in Enum.GetValues<ManeuverKind>())
        {
            for (int distance = 500; distance >= 50; distance -= 50)
                yield return AtDistance(kind, distance);
            yield return Immediate(kind);
        }
    }
}

/// <summary>
/// Own on the serialized navigation loop. Plans are read-only previews. Only valid Update calls
/// record actual samples or consume stages; temporary route loss cannot erase their history.
/// </summary>
public sealed class NavigationInstructionPolicy
{
    private const int MaximumEncounters = 2048;
    private const double CrossingToleranceMeters = 20;
    private const int AllStages = 7;
    private static readonly int[] Stages = [500, 200, 50];
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(2);
    private sealed class Encounter(double distance, DateTimeOffset observedAt, DateTimeOffset now)
    {
        public double MinimumDistance { get; set; } = distance;
        public double? LastUpdateDistance { get; set; }
        public int Consumed { get; set; }
        public DateTimeOffset ObservedAt { get; set; } = observedAt;
        public DateTimeOffset LastSeen { get; set; } = now;
    }
    private readonly record struct Decision(string Text, int Consumed);
    private readonly Dictionary<string, Encounter> encounters = new(StringComparer.Ordinal);
    private string? session;

    public IReadOnlyList<string> Plan(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        if (!Valid(maneuver, sessionId, observedAt, now, active) || maneuver is null) return [];
        Encounter encounter;
        if (StringComparer.Ordinal.Equals(session, sessionId) && encounters.TryGetValue(maneuver.Id, out var known))
        {
            if (observedAt < known.ObservedAt) return [];
            encounter = Reapproaching(known, maneuver.DistanceMeters)
                ? new(maneuver.DistanceMeters, observedAt, now) : known;
        }
        else encounter = new(maneuver.DistanceMeters, observedAt, now);

        // Preview the current due instruction without mutating this encounter or LastUpdateDistance.
        var due = Select(encounter, maneuver.Kind, maneuver.DistanceMeters);
        int remaining = due?.Consumed ?? encounter.Consumed;
        var result = new List<string>(11);
        if (due is { } current) result.Add(current.Text);
        for (int index = 0; index < Stages.Length; index++)
            if ((remaining & (1 << index)) == 0)
                result.Add(NavigationPrompts.AtDistance(maneuver.Kind, Stages[index]));
        // Preload rounded catch-up variants too, since a future matching gap can skip a boundary.
        if ((remaining & 1) == 0)
            foreach (int distance in new[] { 450, 400, 350, 300, 250 })
                result.Add(NavigationPrompts.AtDistance(maneuver.Kind, distance));
        if ((remaining & 2) == 0)
            foreach (int distance in new[] { 150, 100, 50 })
                result.Add(NavigationPrompts.AtDistance(maneuver.Kind, distance));
        if (remaining != AllStages) result.Add(NavigationPrompts.Immediate(maneuver.Kind));
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Update(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        if (!Valid(maneuver, sessionId, observedAt, now, active) || maneuver is null) return [];
        if (!StringComparer.Ordinal.Equals(session, sessionId))
        {
            Reset();
            session = sessionId;
        }
        if (encounters.TryGetValue(maneuver.Id, out var encounter))
        {
            if (observedAt < encounter.ObservedAt) return [];
            if (Reapproaching(encounter, maneuver.DistanceMeters))
                encounters[maneuver.Id] = encounter = new(maneuver.DistanceMeters, observedAt, now);
        }
        else
        {
            if (encounters.Count >= MaximumEncounters)
            {
                string oldest = encounters.OrderBy(p => p.Value.LastSeen)
                    .ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                encounters.Remove(oldest);
            }
            encounters[maneuver.Id] = encounter = new(maneuver.DistanceMeters, observedAt, now);
        }

        var decision = Select(encounter, maneuver.Kind, maneuver.DistanceMeters);
        encounter.LastUpdateDistance = maneuver.DistanceMeters;
        encounter.MinimumDistance = Math.Min(encounter.MinimumDistance, maneuver.DistanceMeters);
        encounter.ObservedAt = observedAt;
        encounter.LastSeen = now;
        if (decision is not { } instruction) return [];
        encounter.Consumed = instruction.Consumed;
        return [instruction.Text];
    }

    private static Decision? Select(Encounter encounter, ManeuverKind kind, double distance)
    {
        if (distance > Stages[0]) return null;
        int index = distance > 200 ? 0 : distance > 50 ? 1 : 2;
        if ((encounter.Consumed & (1 << index)) != 0) return null;
        int consumed = encounter.Consumed | ((1 << (index + 1)) - 1);
        if (distance < 50 && encounter.Consumed == 0)
            return new(NavigationPrompts.Immediate(kind), AllStages);

        int standard = Stages[index];
        int nextPending = 0;
        while ((encounter.Consumed & (1 << nextPending)) != 0) nextPending++;
        bool normalCrossing = index == nextPending
            && encounter.LastUpdateDistance is { } previous && previous > standard
            && distance >= standard - CrossingToleranceMeters;
        if (distance == standard || normalCrossing)
            return new(NavigationPrompts.AtDistance(kind, standard), consumed);
        if (distance < 50) return new(NavigationPrompts.Immediate(kind), AllStages);

        int rounded = (int)Math.Floor(distance / 50) * 50;
        // A catch-up warning also consumes the immediately following lower standard stage.
        // For example, 275 -> 250 skips 200; 79 -> 50 skips the later 50 m warning.
        consumed |= (1 << Math.Min(index + 2, Stages.Length)) - 1;
        return new(NavigationPrompts.AtDistance(kind, rounded), consumed);
    }

    private static bool Reapproaching(Encounter encounter, double distance)
        => encounter.MinimumDistance <= 50 && distance > 550;

    private static bool Valid(NavigationManeuver? maneuver, string sessionId,
        DateTimeOffset observedAt, DateTimeOffset now, bool active)
    {
        var age = now - observedAt;
        return active && age >= TimeSpan.Zero && age <= MaximumAge && !string.IsNullOrWhiteSpace(sessionId)
            && maneuver is not null && !string.IsNullOrWhiteSpace(maneuver.Id)
            && Enum.IsDefined(maneuver.Kind) && double.IsFinite(maneuver.DistanceMeters)
            && maneuver.DistanceMeters >= 0;
    }

    public void Reset()
    {
        encounters.Clear();
        session = null;
    }
}
