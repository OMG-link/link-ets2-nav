using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Map;

/// <summary>Explicit evidence-backed local transforms; never stores world-space trigger points.</summary>
public sealed class CameraCalibrationSet
{
    private readonly Dictionary<(string Descriptor, string Hookup, string Locator, string Variant), Entry> entries;
    public static CameraCalibrationSet Empty { get; } = new([]);
    public int Count => entries.Count;

    private CameraCalibrationSet(IEnumerable<Entry> source)
    {
        entries = new();
        foreach (var entry in source)
        {
            Validate(entry);
            if (!entries.TryAdd((entry.Descriptor, entry.Hookup, entry.Locator, entry.Variant), entry))
                throw new InvalidDataException("Duplicate camera calibration key.");
        }
    }

    public static CameraCalibrationSet Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Camera calibration file exceeds 1 MiB.");
        return Parse(File.ReadAllText(path));
    }

    public static CameraCalibrationSet Parse(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("Camera calibration file is empty.");
            if (document.SchemaVersion != 1 || document.Entries is null || document.Entries.Length > 4096)
                throw new InvalidDataException("Unsupported camera calibration schema or entry count.");
            return new CameraCalibrationSet(document.Entries);
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid camera calibration JSON: " + ex.Message, ex); }
    }

    public CameraObservation Apply(CameraObservation camera)
    {
        // Incoming unverified placement remains unverified unless every binding matches.
        if (!camera.ActivePartVerified || !entries.TryGetValue((camera.SourceDescriptor, camera.Hookup,
                camera.LocatorName, camera.ModelVariant), out var entry)
            || !StringComparer.OrdinalIgnoreCase.Equals(camera.MetadataFingerprint, entry.MetadataFingerprint))
            return camera with { DirectionVerified = false };
        var direction = Vector3.TransformNormal(Axis(entry.TravelAxis), camera.LocalToWorld);
        var offset = Vector3.TransformNormal(new(entry.MonitoredOffset.X, entry.MonitoredOffset.Y, entry.MonitoredOffset.Z), camera.LocalToWorld);
        var position = camera.LocatorPosition + offset;
        if (!Finite(direction) || direction.LengthSquared() < 0.001f || !Finite(position))
            return camera with { DirectionVerified = false };
        return camera with { TravelDirection = Vector3.Normalize(direction), MonitoredPosition = position, DirectionVerified = true };
    }

    private static void Validate(Entry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Descriptor) || !entry.Descriptor.StartsWith('/')
            || !entry.Descriptor.EndsWith(".pmd", StringComparison.Ordinal) || entry.Descriptor.Contains("..", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.Hookup) || string.IsNullOrWhiteSpace(entry.Locator)
            || string.IsNullOrWhiteSpace(entry.Variant) || string.IsNullOrWhiteSpace(entry.Evidence)
            || entry.Evidence.Length > 2048 || entry.MetadataFingerprint is null || entry.MetadataFingerprint.Length != 64
            || !entry.MetadataFingerprint.All(Uri.IsHexDigit) || entry.MonitoredOffset is null)
            throw new InvalidDataException("Calibration requires descriptor, hookup, locator, variant, SHA256 fingerprint, offset and evidence.");
        _ = Axis(entry.TravelAxis);
        if (!Finite(new(entry.MonitoredOffset.X, entry.MonitoredOffset.Y, entry.MonitoredOffset.Z)))
            throw new InvalidDataException("Camera calibration offsets must be finite.");
    }

    private static Vector3 Axis(string axis) => axis switch
    {
        "+X" => Vector3.UnitX, "-X" => -Vector3.UnitX,
        "+Z" => Vector3.UnitZ, "-Z" => -Vector3.UnitZ,
        _ => throw new InvalidDataException("Camera travelAxis must be +X, -X, +Z or -Z in locator-local space.")
    };
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class Document
    {
        public required int SchemaVersion { get; init; }
        public required Entry[] Entries { get; init; }
    }
    private sealed class Entry
    {
        public required string Descriptor { get; init; }
        public required string Hookup { get; init; }
        public required string Locator { get; init; }
        public required string Variant { get; init; }
        public required string MetadataFingerprint { get; init; }
        public required string TravelAxis { get; init; }
        public required Offset MonitoredOffset { get; init; }
        public required string Evidence { get; init; }
    }
    private sealed class Offset
    {
        public required float X { get; init; }
        public required float Y { get; init; }
        public required float Z { get; init; }
    }
}
