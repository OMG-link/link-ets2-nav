using System.Numerics;

namespace LinkEts2Nav.Map;

/// <summary>A real speed-camera hookup, not a camera-looking decorative model.</summary>
public sealed record CameraObservation(
    string Id,
    Vector3 LocatorPosition,
    Vector3 MonitoredPosition,
    Vector3 TravelDirection,
    bool DirectionVerified,
    string SourceDescriptor,
    string Hookup)
{
    public string LocatorName { get; init; } = "";
    public string ModelVariant { get; init; } = "";
    public string MetadataFingerprint { get; init; } = "";
    public bool ActivePartVerified { get; init; }
    public Matrix4x4 LocalToWorld { get; init; } = Matrix4x4.Identity;
    public float MeasurementDistanceMeters { get; init; }
    public bool ReverseDirection { get; init; }
    public Quaternion LocatorRotation { get; init; }
}

public sealed record SpeedCameraLocator(
    string Name, string Hookup, Vector3 Position, Quaternion Rotation,
    float MeasurementDistance, bool ReverseDirection)
{
    public int PartIndex { get; init; } = -1;
    public float Scale { get; init; } = 1;
}
