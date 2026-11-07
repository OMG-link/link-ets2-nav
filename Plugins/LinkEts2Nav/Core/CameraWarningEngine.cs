namespace LinkEts2Nav.Core;

/// <summary>Evidence produced by directed road/route matching, never by proximity alone.</summary>
public enum CameraApproach
{
    Unknown,
    RouteAhead,
    UnavoidableAhead,
    Behind
}

public enum WarningDistanceKind { Road, Euclidean }

/// <summary>Distances refer to the monitored road position, not the roadside camera model.</summary>
public sealed record CameraCandidate(
    string Id,
    CameraApproach Approach,
    double EuclideanDistanceMeters,
    double? RoadDistanceMeters = null)
{
    public string? MatchFailure { get; init; }
    public int? SpeedLimitKph { get; init; }
    // A conservative route-length bound, never an asserted complete road distance.
    public double? RouteDistanceLowerBoundMeters { get; init; }
}

/// <summary>A consistent input sample. SessionId must change after teleport, load or map change.</summary>
public sealed record WarningSnapshot(
    string SessionId,
    string RouteRevision,
    bool HasNavigationRoute,
    bool IsActive,
    DateTimeOffset ObservedAt,
    IReadOnlyList<CameraCandidate> Candidates);

public sealed record CameraWarning(
    string CameraId,
    double DistanceMeters,
    WarningDistanceKind DistanceKind)
{
    public double StageDistanceMeters { get; init; } = 500;
    public bool FirstConfirmedInside200Meters { get; init; }
    public int? SpeedLimitKph { get; init; }
}

/// <summary>A pending stage ordered by remaining travel before its trigger, not by camera proximity.</summary>
public sealed record PlannedCameraWarning(
    string CameraId,
    double StageDistanceMeters,
    double DistanceMeters,
    WarningDistanceKind DistanceKind,
    double DistanceUntilTriggerMeters)
{
    public bool FirstConfirmedInside200Meters { get; init; }
    public int? SpeedLimitKph { get; init; }
}

/// <summary>
/// Pure decision logic; does not read the game, play audio or infer road membership.
/// Own one instance per player and serialize GetPlans/Update/Reset calls on the plugin update loop.
/// </summary>
public sealed class CameraWarningEngine
{
    private readonly double[] stages;
    private readonly double rearmDistance;
    private readonly TimeSpan maximumAge;
    private const double PassageRadiusMeters = 20;
    private readonly HashSet<string> nearPassage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> consumed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> departedPeaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> firstConfirmedDistances = new(StringComparer.Ordinal);
    private string? sessionId;
    private int AllStages => (1 << stages.Length) - 1;

    public CameraWarningEngine(
        double triggerDistanceMeters = 500,
        double rearmDistanceMeters = 50,
        TimeSpan? maximumAge = null)
    {
        if (!double.IsFinite(triggerDistanceMeters) || triggerDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerDistanceMeters));
        if (!double.IsFinite(rearmDistanceMeters) || rearmDistanceMeters <= PassageRadiusMeters)
            throw new ArgumentOutOfRangeException(nameof(rearmDistanceMeters));
        this.maximumAge = maximumAge ?? TimeSpan.FromSeconds(2);
        if (this.maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        // A shorter custom first threshold stays a single stage rather than creating
        // a second warning farther away than the caller's requested first warning.
        stages = triggerDistanceMeters > 200 ? [triggerDistanceMeters, 200] : [triggerDistanceMeters];
        rearmDistance = rearmDistanceMeters;
    }

    /// <summary>
    /// Returns all pending stages for currently proven forward targets. Planning does not
    /// consume a stage. Stale/paused samples cannot change encounter lifecycle either.
    /// A late first observation inside the final threshold omits the obsolete first stage.
    /// </summary>
    public IReadOnlyList<PlannedCameraWarning> GetPlans(WarningSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var age = now - snapshot.ObservedAt;
        if (!snapshot.IsActive || age < TimeSpan.Zero || age > maximumAge)
            return [];
        if (!StringComparer.Ordinal.Equals(snapshot.SessionId, sessionId))
        {
            Reset();
            sessionId = snapshot.SessionId;
        }

        var result = new List<PlannedCameraWarning>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in snapshot.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id)
                || !double.IsFinite(candidate.EuclideanDistanceMeters) || candidate.EuclideanDistanceMeters < 0)
                continue;

            var required = snapshot.HasNavigationRoute ? CameraApproach.RouteAhead : CameraApproach.UnavoidableAhead;
            double distance = candidate.RoadDistanceMeters ?? candidate.EuclideanDistanceMeters;
            double lowerBound = candidate.RouteDistanceLowerBoundMeters ?? 0;
            if (!double.IsFinite(lowerBound) || lowerBound < 0) continue;
            double triggerDistance = Math.Max(distance, lowerBound);
            // Hysteresis: a proven approach/pass must enter the 20 m vicinity before
            // leaving >=50 m can rearm. Large distance alone cannot repeat a warning.
            if (candidate.EuclideanDistanceMeters <= PassageRadiusMeters
                && (candidate.Approach == CameraApproach.Behind
                    || candidate.Approach == required && triggerDistance <= PassageRadiusMeters))
                nearPassage.Add(candidate.Id);
            if (candidate.EuclideanDistanceMeters >= rearmDistance && nearPassage.Remove(candidate.Id))
            {
                departedPeaks[candidate.Id] = candidate.EuclideanDistanceMeters;
            }
            if (departedPeaks.TryGetValue(candidate.Id, out double peak))
            {
                peak = Math.Max(peak, candidate.EuclideanDistanceMeters);
                departedPeaks[candidate.Id] = peak;
                // Leaving the 50 m boundary is not itself a new approach. Require a
                // signed continuous-road match, or at least 5 m of renewed approach.
                bool approaching = candidate.RoadDistanceMeters is > 0
                    || candidate.EuclideanDistanceMeters <= peak - 5;
                if (candidate.Approach != required || !approaching) continue;
                departedPeaks.Remove(candidate.Id);
                consumed.Remove(candidate.Id);
                firstConfirmedDistances.Remove(candidate.Id);
            }
            if (candidate.Approach == CameraApproach.Behind)
            {
                if (nearPassage.Contains(candidate.Id)) consumed[candidate.Id] = AllStages;
                continue;
            }
            if (candidate.Approach != required) continue;
            if (!double.IsFinite(distance) || distance <= 0) continue;

            firstConfirmedDistances.TryAdd(candidate.Id, triggerDistance);
            consumed.TryGetValue(candidate.Id, out int mask);
            // If both boundaries were crossed between samples, only the nearer stage
            // remains relevant. Do not queue an already obsolete 500 m announcement.
            int firstRelevant = 0;
            for (int index = 1; index < stages.Length; index++)
                if (triggerDistance <= stages[index]) firstRelevant = index;
            for (int index = firstRelevant; index < stages.Length; index++)
            {
                if ((mask & (1 << index)) != 0) continue;
                result.Add(new(candidate.Id, stages[index], distance,
                    candidate.RoadDistanceMeters.HasValue ? WarningDistanceKind.Road : WarningDistanceKind.Euclidean,
                    Math.Max(0, triggerDistance - stages[index]))
                {
                    FirstConfirmedInside200Meters = firstConfirmedDistances[candidate.Id] < 200,
                    SpeedLimitKph = candidate.SpeedLimitKph
                });
            }
        }
        return result.OrderBy(plan => plan.DistanceUntilTriggerMeters)
            .ThenBy(plan => plan.DistanceMeters)
            .ThenBy(plan => plan.CameraId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<CameraWarning> Update(WarningSnapshot snapshot, DateTimeOffset now)
    {
        var result = new List<CameraWarning>();
        foreach (var plan in GetPlans(snapshot, now))
        {
            if (plan.DistanceUntilTriggerMeters > 0) continue;
            int index = Array.IndexOf(stages, plan.StageDistanceMeters);
            consumed.TryGetValue(plan.CameraId, out int mask);
            // Consuming the final stage also expires all earlier stages.
            consumed[plan.CameraId] = mask | ((1 << (index + 1)) - 1);
            result.Add(new CameraWarning(plan.CameraId, plan.DistanceMeters, plan.DistanceKind)
            {
                StageDistanceMeters = plan.StageDistanceMeters,
                FirstConfirmedInside200Meters = plan.FirstConfirmedInside200Meters,
                SpeedLimitKph = plan.SpeedLimitKph
            });
        }
        return result;
    }

    public void Reset()
    {
        consumed.Clear();
        nearPassage.Clear();
        firstConfirmedDistances.Clear();
        departedPeaks.Clear();
        sessionId = null;
    }
}
