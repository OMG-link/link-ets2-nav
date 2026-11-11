namespace LinkEts2Nav.Runtime;

public sealed record CameraDiagnostic(string Id, string Hookup, double DistanceMeters,
    bool DirectionVerified, string Decision, double? RoadDistanceMeters = null,
    string SourceDescriptor = "", string Variant = "", string MetadataFingerprint = "",
    double? RouteDistanceLowerBoundMeters = null, int? SpeedLimitKph = null, string? SpeedLimitReason = null);

public sealed record NavDiagnosticSnapshot(DateTimeOffset UpdatedAt, string State,
    string RouteState, int CameraCount, IReadOnlyList<CameraDiagnostic> Cameras, string? LastWarning = null, string? VehiclePosition = null, FineObservation? LatestFine = null,
    FineObservation? LatestCameraFine = null, IReadOnlyList<FineObservation>? FineHistory = null, long DroppedFineCount = 0)
{
    public CurrentSpeedLimitDiagnostic? CurrentSpeedLimit { get; init; }
    public NavigationDiagnostic? Navigation { get; init; }
    public GuidanceDiagnostic? Guidance { get; init; }
}

/// <summary>Read-only in-process diagnostics shared with the plugin's Razor page.</summary>
public static class NavDiagnostics
{
    private static NavDiagnosticSnapshot current = new(default, "插件尚未启用。", "Unknown", 0, []);
    public static NavDiagnosticSnapshot Current => Volatile.Read(ref current);
    public static void Publish(NavDiagnosticSnapshot value) => Volatile.Write(ref current, value);
}
