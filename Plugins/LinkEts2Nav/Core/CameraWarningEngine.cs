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
    double? RoadDistanceMeters = null);

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
    WarningDistanceKind DistanceKind);

/// <summary>
/// Pure decision logic; does not read the game, play audio or infer road membership.
/// Own one instance per player and serialize Update/Reset calls on the plugin update loop.
/// </summary>
public sealed class CameraWarningEngine
{
    private readonly double _triggerDistance;
    private readonly double _rearmDistance;
    private readonly TimeSpan _maximumAge;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private string? _sessionId;

    public CameraWarningEngine(
        double triggerDistanceMeters = 300,
        double rearmDistanceMeters = 600,
        TimeSpan? maximumAge = null)
    {
        if (!double.IsFinite(triggerDistanceMeters) || triggerDistanceMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(triggerDistanceMeters));
        if (!double.IsFinite(rearmDistanceMeters) || rearmDistanceMeters <= triggerDistanceMeters)
            throw new ArgumentOutOfRangeException(nameof(rearmDistanceMeters));
        _maximumAge = maximumAge ?? TimeSpan.FromSeconds(2);
        if (_maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        _triggerDistance = triggerDistanceMeters;
        _rearmDistance = rearmDistanceMeters;
    }

    public IReadOnlyList<CameraWarning> Update(WarningSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Paused/stale samples cannot trigger, rearm, or reset an encounter.
        var age = now - snapshot.ObservedAt;
        if (!snapshot.IsActive || age < TimeSpan.Zero || age > _maximumAge)
            return Array.Empty<CameraWarning>();

        if (!StringComparer.Ordinal.Equals(snapshot.SessionId, _sessionId))
        {
            Reset();
            _sessionId = snapshot.SessionId;
        }

        var result = new List<CameraWarning>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in snapshot.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id) || !seen.Add(candidate.Id)
                || !double.IsFinite(candidate.EuclideanDistanceMeters)
                || candidate.EuclideanDistanceMeters < 0)
                continue;

            // Require physical separation before another encounter can warn. A route
            // recalculation, uncertain sample or brief disappearance must not cause repeats.
            if (candidate.EuclideanDistanceMeters >= _rearmDistance)
                _announced.Remove(candidate.Id);

            var requiredApproach = snapshot.HasNavigationRoute
                ? CameraApproach.RouteAhead : CameraApproach.UnavoidableAhead;
            if (candidate.Approach != requiredApproach || _announced.Contains(candidate.Id))
                continue;

            var distance = candidate.RoadDistanceMeters ?? candidate.EuclideanDistanceMeters;
            // Invalid supplied road distance is not permission to fall back to proximity.
            if (!double.IsFinite(distance) || distance <= 0 || distance > _triggerDistance)
                continue;

            result.Add(new CameraWarning(candidate.Id, distance,
                candidate.RoadDistanceMeters.HasValue ? WarningDistanceKind.Road : WarningDistanceKind.Euclidean));
            _announced.Add(candidate.Id);
        }
        return result;
    }

    public void Reset()
    {
        _announced.Clear();
        _sessionId = null;
    }
}
