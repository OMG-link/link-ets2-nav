using System.Globalization;
using System.Numerics;
using PrefabDescriptor = TruckLib.Models.Ppd.PrefabDescriptor;
using LinkEts2Nav.Core;
using System.Security.Cryptography;
using System.Text;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
using ScsMap = TruckLib.ScsMap.Map;
using ScsModel = TruckLib.ScsMap.Model;

namespace LinkEts2Nav.Map;

public sealed record NearbyMapSnapshot(ScsMap Map, IReadOnlyList<CameraObservation> Cameras,
    IReadOnlyList<string> Diagnostics, IReadOnlyList<SectorCoordinate> Sectors)
{
    public IReadOnlyList<DirectedRoad> PrefabPaths { get; init; } = [];
    public IReadOnlyList<RoundaboutStructure> Roundabouts { get; init; } = [];
    public IReadOnlyDictionary<ulong, RoundaboutStructure> RoadStructures { get; init; } = new Dictionary<ulong, RoundaboutStructure>();
    public SpeedLimitData SpeedLimits { get; init; } = SpeedLimitData.Empty;
    public IReadOnlyList<FreeNavigationArea> FreeNavigationAreas { get; init; } = [];
}

/// <summary>
/// Own one instance per mounted map. Call on a background worker; instances are not thread-safe.
/// Only the nearby/route sector window is retained; overlapping prefab geometry and model metadata are reused.
/// </summary>
public sealed class NearbyCameraScanner
{
    private readonly RoundaboutMapIndex roundaboutIndex = new();
    private readonly IFileSystem fileSystem;
    private readonly ScsMap? hostMap;
    private readonly HostMapView? hostView;
    private readonly string mapPath;
    private readonly Dictionary<string, string> prefabPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PrefabDescriptor?> prefabDescriptors = new(StringComparer.Ordinal);
    private IReadOnlyList<DirectedRoad> junctionGeometry = [];
    private IReadOnlyList<FreeNavigationArea> freeNavigationAreas = [];
    private string[] junctionDiagnostics = [];
    private Dictionary<ulong, (IReadOnlyList<DirectedRoad> Paths, IReadOnlyList<FreeNavigationArea> Areas)> geometryCache = [];
    private readonly Dictionary<string, string> modelPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> signPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float MeasurementDistance, bool ReverseDirection)> cameraDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SpeedCameraLocator>> locatorCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), (IReadOnlySet<int> Visible, string Fingerprint)> variantCache = new();
    private readonly List<string> definitionDiagnostics = [];
    private readonly Dictionary<string, string> locatorFailures = new(StringComparer.Ordinal);
    private ScsMap? map;
    private SectorCoordinate[] loadedSectors = [];
    private bool definitionsLoaded;
    private readonly IReadOnlyList<string> activeModPaths;
    private SpeedLimitDefinitionReader? speedReader;
    private SpeedLimitData speedLimits = SpeedLimitData.Empty;

    public NearbyCameraScanner(IFileSystem fileSystem, string mapPath = "/map/europe.mbd", IReadOnlyList<string>? activeModPaths = null, ScsMap? hostMap = null)
    {
        this.fileSystem = fileSystem;
        this.hostMap = hostMap;
        hostView = hostMap is null ? null : new HostMapView(hostMap);
        this.mapPath = mapPath;
        this.activeModPaths = activeModPaths ?? [];
    }

    public void Prepare(CancellationToken cancellationToken = default)
    {
        LoadDefinitions(cancellationToken);
        speedReader!.PrepareHost(hostMap, cancellationToken);
    }

    public NearbyMapSnapshot ReadNearby(Vector3 playerPosition, float radiusMeters = 1000,
        CancellationToken cancellationToken = default, MapScanCoverage? coverage = null)
    {
        if (!float.IsFinite(radiusMeters) || radiusMeters <= 0 || radiusMeters > 4000)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        if (!float.IsFinite(playerPosition.X) || !float.IsFinite(playerPosition.Z))
            throw new ArgumentException("Player placement must be finite.", nameof(playerPosition));
        cancellationToken.ThrowIfCancellationRequested();
        Prepare(cancellationToken);
        // Halo covers references and locators on models whose origins are in an adjacent sector.
        coverage ??= MapScanCoverage.Create(playerPosition, radiusMeters, null, null, 0);
        var sectors = coverage.Sectors.ToArray();
        if (map == null || !sectors.SequenceEqual(loadedSectors))
        {
            var nextMap = new CameraMap(hostMap);
            nextMap.Read(mapPath, fileSystem, sectors);
            hostView?.MergeInto(nextMap, sectors);
            cancellationToken.ThrowIfCancellationRequested();
            var junctions = new List<DirectedRoad>();
            var areas = new List<FreeNavigationArea>();
            var issues = new List<string>();
            var nextGeometry = new Dictionary<ulong, (IReadOnlyList<DirectedRoad> Paths, IReadOnlyList<FreeNavigationArea> Areas)>();
            foreach (var prefab in nextMap.MapItems.Values.OfType<Prefab>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!prefabPaths.TryGetValue(prefab.Model.ToString(), out string? resource)) continue;
                try
                {
                    if (!prefabDescriptors.TryGetValue(resource, out var descriptor))
                        prefabDescriptors[resource] = descriptor = PrefabDescriptor.Open(resource, fileSystem);
                    if (descriptor is not null)
                    {
                        roundaboutIndex.AddPrefab(prefab, descriptor);
                        if (!geometryCache.TryGetValue(prefab.Uid, out var geometry))
                            geometry = (PrefabPathGeometry.Build(prefab, descriptor), FreeNavigationArea.Build(prefab,descriptor));
                        nextGeometry[prefab.Uid] = geometry;
                        junctions.AddRange(geometry.Paths);
                        areas.AddRange(geometry.Areas);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    prefabDescriptors[resource] = null;
                    issues.Add($"Prefab {resource}: {ex.Message}");
                }
            }
            foreach(var road in nextMap.MapItems.Values.OfType<Road>()) roundaboutIndex.AddRoad(road);
            roundaboutIndex.BuildNewSectors(sectors,cancellationToken);
            geometryCache = nextGeometry;
            junctionGeometry = junctions;
            freeNavigationAreas = areas;
            junctionDiagnostics = issues.Distinct().ToArray();
            map = nextMap;
            loadedSectors = sectors;
            speedLimits = speedReader!.Build(nextMap,cancellationToken,hostMap);
        }
        var diagnostics = new List<string>(definitionDiagnostics);
        diagnostics.AddRange(junctionDiagnostics);
        if(speedLimits.Error is not null) diagnostics.Add(speedLimits.Error);
        var cameras = new List<CameraObservation>();
        foreach (var item in map.MapItems.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is not SingleNodeItem placed || placed.Node is not Node node) continue;
            if (!coverage.Contains(node.Position, 100)) continue;
            string? descriptor = null;
            Vector3 scale = Vector3.One;
            string variant = "default";
            if (item is ScsModel modelItem)
            {
                if (!modelItem.ModelHookups) continue;
                modelPaths.TryGetValue(modelItem.Name.ToString(), out descriptor);
                scale = modelItem.Scale;
                variant = modelItem.Variant.ToString();
            }
            else if (item is Sign signItem)
            {
                signPaths.TryGetValue(signItem.Model.ToString(), out descriptor);
                variant = signItem.Variant.ToString();
                if (signItem.FollowRoadDir)
                {
                    diagnostics.Add($"Sign {item.Uid:x}: runtime FollowRoadDir placement cannot be reproduced yet.");
                    continue;
                }
            }
            if (descriptor == null) continue;
            if (string.IsNullOrEmpty(variant)) variant = "default";
            string failureKey = descriptor + "|" + variant;
            if (locatorFailures.TryGetValue(failureKey, out var previousFailure))
            {
                diagnostics.Add(previousFailure);
                continue;
            }
            try
            {
                var locators = GetLocators(descriptor);
                if (locators.Count == 0) continue;
                var active = GetActiveParts(descriptor, variant);
                foreach (var locator in locators)
                {
                    if (!active.Visible.Contains(locator.PartIndex)) continue;
                    Vector3 worldPosition = node.Position + Vector3.Transform(locator.Position * scale, node.Rotation);
                    if (!coverage.Contains(worldPosition)) continue;
                    var worldRotation = Quaternion.Normalize(node.Rotation * locator.Rotation);
                    // Axis sign is deliberately unverified until in-game calibration.
                    Vector3 direction = Vector3.Transform(-Vector3.UnitZ, worldRotation);
                    if (locator.ReverseDirection) direction = -direction;
                    cameras.Add(new CameraObservation($"{item.Uid:x}:{locator.Name}", worldPosition,
                        worldPosition, direction, false, descriptor, locator.Hookup)
                    {
                        LocatorName = locator.Name,
                        ModelVariant = variant,
                        ActivePartVerified = true,
                        MetadataFingerprint = Fingerprint(descriptor, variant, locator, active.Fingerprint),
                        LocalToWorld = Matrix4x4.CreateScale(locator.Scale) * Matrix4x4.CreateFromQuaternion(locator.Rotation) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(node.Rotation),
                        MeasurementDistanceMeters = locator.MeasurementDistance,
                        ReverseDirection = locator.ReverseDirection,
                        LocatorRotation = worldRotation
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string message = $"{descriptor}: {ex.Message}";
                locatorFailures[failureKey] = message;
                diagnostics.Add(message);
            }
        }
        return new NearbyMapSnapshot(map, cameras, diagnostics.Distinct().ToArray(), loadedSectors) { RoadStructures = roundaboutIndex.RoadAnnotations, Roundabouts = roundaboutIndex.Structures, PrefabPaths = junctionGeometry, SpeedLimits = speedLimits, FreeNavigationAreas = freeNavigationAreas };
    }

    public static SectorCoordinate[] SectorsNear(Vector3 position, float radiusMeters)
    {
        if (!float.IsFinite(radiusMeters) || radiusMeters < 0 || radiusMeters > 4100)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        const double maximumCoordinate = (int.MaxValue - 4d) * ScsMap.SectorSize;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)
            || Math.Abs((double)position.X) > maximumCoordinate || Math.Abs((double)position.Z) > maximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(position));
        int minX = (int)MathF.Floor((position.X - radiusMeters) / ScsMap.SectorSize);
        int maxX = (int)MathF.Floor((position.X + radiusMeters) / ScsMap.SectorSize);
        int minZ = (int)MathF.Floor((position.Z - radiusMeters) / ScsMap.SectorSize);
        int maxZ = (int)MathF.Floor((position.Z + radiusMeters) / ScsMap.SectorSize);
        var result = new List<SectorCoordinate>();
        for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++) result.Add(new(x, z));
        return result.ToArray();
    }

    private IReadOnlyList<SpeedCameraLocator> GetLocators(string descriptor)
    {
        if (locatorCache.TryGetValue(descriptor, out var cached)) return cached;
        using var stream = fileSystem.Open(Path.ChangeExtension(descriptor, ".pmg"));
        var locators = PmgLocatorReader.Read(stream, cameraDefinitions);
        locatorCache.Add(descriptor, locators);
        return locators;
    }

    private (IReadOnlySet<int> Visible, string Fingerprint) GetActiveParts(string descriptor, string variant)
    {
        if (variantCache.TryGetValue((descriptor, variant), out var cached)) return cached;
        using var stream = fileSystem.Open(descriptor);
        if (!stream.CanSeek || stream.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Unsupported PMD metadata stream.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var result = (PmdVariantReader.ReadVisibleParts(bytes, variant), Convert.ToHexString(SHA256.HashData(bytes)));
        variantCache.Add((descriptor, variant), result);
        return result;
    }

    private static string Fingerprint(string descriptor, string variant, SpeedCameraLocator locator, string pmdHash)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(descriptor); writer.Write(variant); writer.Write(locator.Name); writer.Write(locator.Hookup);
            writer.Write(locator.Position.X); writer.Write(locator.Position.Y); writer.Write(locator.Position.Z);
            writer.Write(locator.Rotation.X); writer.Write(locator.Rotation.Y); writer.Write(locator.Rotation.Z); writer.Write(locator.Rotation.W);
            writer.Write(locator.PartIndex); writer.Write(locator.Scale); writer.Write(locator.MeasurementDistance); writer.Write(locator.ReverseDirection);
            writer.Write(pmdHash);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private void LoadDefinitions(CancellationToken cancellationToken)
    {
        if (definitionsLoaded) return;
        foreach (var path in fileSystem.GetFiles("/unit/hookup/").Where(p => p.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var unit in SiiFile.Open(path, fileSystem).Units)
                {
                    if (unit.Class != "speed_camera") continue;
                    unit.Attributes.TryGetValue("measurement_distance", out var value);
                    unit.Attributes.TryGetValue("reverse_direction", out var reversed);
                    cameraDefinitions[unit.Name] = ReadDefinitionValues((object?)value, (object?)reversed);
                }
            }
            catch (Exception ex) { definitionDiagnostics.Add($"{path}: {ex.Message}"); }
        }
        foreach (var path in fileSystem.GetFiles("/def/world/").Where(p => p.EndsWith(".sii", StringComparison.OrdinalIgnoreCase) &&
                     (Path.GetFileName(p).StartsWith("model.", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("sign.", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("prefab.", StringComparison.Ordinal))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var unit in SiiFile.Open(path, fileSystem).Units)
                {
                    if (unit.Class == "prefab_model" && unit.Attributes.TryGetValue("prefab_desc", out var prefabDescriptor))
                    {
                        prefabPaths[unit.Name.Split('.').Last()] = Convert.ToString((object)prefabDescriptor, CultureInfo.InvariantCulture)?.Trim('"') ?? "";
                        continue;
                    }
                    if (!unit.Attributes.TryGetValue("model_desc", out var descriptor)) continue;
                    var token = unit.Name.Split('.').Last();
                    if (unit.Class == "model_def") modelPaths[token] = Convert.ToString((object)descriptor, CultureInfo.InvariantCulture)?.Trim('"') ?? "";
                    else if (unit.Class == "sign_model") signPaths[token] = Convert.ToString((object)descriptor, CultureInfo.InvariantCulture)?.Trim('"') ?? "";
                }
            }
            catch (Exception ex) { definitionDiagnostics.Add($"{path}: {ex.Message}"); }
        }
        speedReader = new SpeedLimitDefinitionReader(fileSystem, activeModPaths);
        definitionsLoaded = true;
    }

    /// <summary>SII attributes are parsed dynamic values, not necessarily source strings.</summary>
    public static (float MeasurementDistance, bool ReverseDirection) ReadDefinitionValues(
        object? measurementDistance, object? reverseDirection)
    {
        float distance = Convert.ToSingle(measurementDistance, CultureInfo.InvariantCulture);
        bool reverse = Convert.ToBoolean(reverseDirection, CultureInfo.InvariantCulture);
        if (!float.IsFinite(distance) || distance < 0)
            throw new InvalidDataException("Speed-camera measurement distance must be finite and nonnegative.");
        return (distance, reverse);
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    private sealed class CameraMap(ScsMap? host) : ScsMap
    {
        protected override bool PostProcessItem(MapItem item) => (item is ScsModel or Sign or Road or Prefab or CityArea or TrafficArea)
            && (host is null || !host.MapItems.ContainsKey(item.Uid));
    }
}
