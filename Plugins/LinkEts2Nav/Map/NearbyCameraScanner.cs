using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using System.Security.Cryptography;
using System.Text;
using TruckLib;
using TruckLib.ScsMap;
using ScsMap = TruckLib.ScsMap.Map;
using ScsModel = TruckLib.ScsMap.Model;

namespace LinkEts2Nav.Map;

public sealed record NearbyMapSnapshot(ScsMap Map, IReadOnlyList<CameraObservation> Cameras,
    IReadOnlyList<string> Diagnostics, IReadOnlyList<SectorCoordinate> Sectors)
{
    public GameDataManager? Data {get;init;}
    public IReadOnlyList<SectorCoordinate> ReadySectors {get;init;}=[];
    public MapScanCoverage? Coverage {get;init;}
    public Func<ulong,MapQueryIssue>? MissingConnection {get;init;}
    public IMapRetryScheduler? Retries {get;init;}
    public IReadOnlyList<MapQueryIssue> MapIssues {get;init;}=[];
    public IReadOnlyList<DirectedRoad> PrefabPaths { get; init; } = [];
    public AuxiliaryLaneIndex? AuxiliaryLanes {get;init;}
    public IReadOnlyList<RoundaboutStructure> Roundabouts { get; init; } = [];
    public IReadOnlyDictionary<ulong, RoundaboutStructure> RoadStructures { get; init; } = new Dictionary<ulong, RoundaboutStructure>();
    public RoadNameIndex? RoadNames {get;init;}
    public CompanyPlaceIndex Companies {get;init;}=CompanyPlaceIndex.Empty;
    public SpeedLimitData SpeedLimits { get; init; } = SpeedLimitData.Empty;
    public FerryJourneyIndex? Ferries {get;init;}
    public IReadOnlyList<FreeNavigationArea> FreeNavigationAreas { get; init; } = [];
    public TrafficSignalMap TrafficSignals {get;init;}=TrafficSignalMap.Empty;
}

/// <summary>
/// Own one instance per mounted map. Call on a background worker; instances are not thread-safe.
/// Derived geometry follows the active window; shared map resources and metadata belong to the dataset.
/// </summary>
public sealed class NearbyCameraScanner
{
    private readonly RoundaboutMapIndex roundaboutIndex = new();
    private readonly GameDataManager data;
    public GameDataManager Data=>data;
    private IReadOnlyList<DirectedRoad> junctionGeometry = [];
    private AuxiliaryLaneIndex? auxiliaryLanes;
    private IReadOnlyList<FreeNavigationArea> freeNavigationAreas = [];
    private TrafficSignalMap trafficSignals=TrafficSignalMap.Empty;
    private string[] junctionDiagnostics = [];
    private Dictionary<(ulong Uid,string Access), (IReadOnlyList<DirectedRoad> Paths, IReadOnlyList<FreeNavigationArea> Areas)> geometryCache = [];
    private ScsMap? map;
    private SectorCoordinate[] loadedSectors = [];
    private IReadOnlyList<MapQueryIssue> mapIssues=[];
    private Func<ulong,MapQueryIssue>? missingConnection;
    private SpeedLimitDefinitionReader speedReader=>data.SpeedLimits;
    private SpeedLimitAreaCache? areaCache;
    private CompanyPlaceIndex companies=CompanyPlaceIndex.Empty;
    private SpeedLimitData speedLimits = SpeedLimitData.Empty;

    public NearbyCameraScanner(IFileSystem fileSystem,string mapPath="/map/europe.mbd",IReadOnlyList<string>? activeModPaths=null,ScsMap? hostMap=null)
        :this(new GameDataManager(fileSystem,activeModPaths,hostMap,mapPath)) { }
    public NearbyCameraScanner(GameDataManager data)=>this.data=data;
    public RouteJourneyEstimator? JourneyEstimator=>data.JourneyEstimator;
    public FerryJourneyIndex? Ferries=>data.Ferries;
    public void Prepare(CancellationToken cancellationToken=default)=>data.Prepare(cancellationToken);

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
            var nextMap=data.ReadWindow(sectors);
            var typedIssues=new List<MapQueryIssue>();
            var represented=nextMap.MapItems.Values.Where(i=>i is Road or Prefab).Select(i=>i.Uid).ToHashSet();
            var readySet=sectors.ToHashSet();
            Func<ulong,MapQueryIssue> need=uid=>data.DescribeMissingConnection(uid,represented,readySet);
            companies=data.Companies.Build(nextMap);
            cancellationToken.ThrowIfCancellationRequested();
            var junctions = new List<DirectedRoad>();
            var signalPrefabs=new List<(Prefab Prefab,TruckLib.Models.Ppd.PrefabDescriptor Descriptor)>();
            var areas = new List<FreeNavigationArea>();
            var issues = new List<string>();
            var nextGeometry = new Dictionary<(ulong Uid,string Access), (IReadOnlyList<DirectedRoad> Paths, IReadOnlyList<FreeNavigationArea> Areas)>();
            var prefabSources=new List<PrefabPermissionSource>();var prefabResources=new Dictionary<ulong,string>();
            foreach(var prefab in nextMap.MapItems.Values.OfType<Prefab>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(!data.World.Prefabs.TryGetValue(prefab.Model.ToString(),out var definition))continue;
                try
                {
                    var descriptor=data.GetPrefabDescription(definition.Descriptor);
                    if(descriptor is not null){prefabSources.Add(new(prefab,descriptor));prefabResources[prefab.Uid]=definition.Descriptor;}
                }
                catch(Exception ex) when(ex is not OperationCanceledException){issues.Add($"Prefab {definition.Descriptor}: {ex.Message}");}
            }
            var permissions=PrefabPlayerAccessWindow.Build(prefabSources,speedReader,data.Map,issues.Add,cancellationToken);
            foreach(var source in prefabSources)
            {
                cancellationToken.ThrowIfCancellationRequested();var prefab=source.Prefab;string resource=prefabResources[prefab.Uid];
                try
                {
                    var access=permissions[prefab.Uid];var key=(prefab.Uid,access.CacheKey);
                    signalPrefabs.Add((prefab,access.Descriptor));
                    roundaboutIndex.AddPrefab(prefab,access.Descriptor,resource,access.CacheKey);
                    if(!geometryCache.TryGetValue(key,out var geometry))
                        geometry=(PrefabPathGeometry.Build(prefab,access.Descriptor),FreeNavigationArea.Build(prefab,source.Descriptor));
                    nextGeometry[key]=geometry;junctions.AddRange(geometry.Paths);areas.AddRange(geometry.Areas);
                }
                catch(Exception ex) when(ex is not OperationCanceledException){issues.Add($"Prefab {resource}: {ex.Message}");}
            }
            foreach(var road in nextMap.MapItems.Values.OfType<Road>()) roundaboutIndex.AddRoad(road,speedReader);
            roundaboutIndex.BuildNewSectors(sectors,cancellationToken,need);
            typedIssues.AddRange(roundaboutIndex.MapIssues);
            areaCache ??= new(data);
            var regionData=areaCache.Update(nextMap,sectors,
                junctions.SelectMany(e=>e.Points).Concat(nextMap.MapItems.Values.OfType<Road>()
                    .SelectMany(r=>Enumerable.Range(0,Math.Clamp((int)Math.Ceiling(r.Length/100),1,256)+1)
                        .Select(i=>r.InterpolateCurve((float)i/Math.Clamp((int)Math.Ceiling(r.Length/100),1,256)).Position))).Append(playerPosition),speedReader!,cancellationToken);
            var builtLimits = ProjectedSignBuilder.Build(data.ReadSpeedLimits(nextMap,regionData,cancellationToken),junctions,cancellationToken);
            trafficSignals=new(signalPrefabs.SelectMany(p=>TrafficSignalMap.Build(p.Prefab,p.Descriptor,data.TrafficSignals,builtLimits)));
            // One annotated geometry graph per map version, shared with navigation consumers.
            var navigation=new RoadPathSource(nextMap,junctions,playerAccess:speedReader.RoadPlayerAccess,roadHalfWidth:speedReader.ReferenceHalfWidth,missingConnection:need,reportMapIssue:typedIssues.Add);
            var builtAuxiliary=AuxiliaryLaneIndex.Build(new NativeLaneGraph(navigation.GetNavigationGraph()),cancellationToken,need);
            typedIssues.AddRange(navigation.MapIssues);
            typedIssues.AddRange(builtAuxiliary.MapIssues);
            cancellationToken.ThrowIfCancellationRequested();
            geometryCache = nextGeometry;
            junctionGeometry = junctions;
            freeNavigationAreas = areas;
            junctionDiagnostics = issues.Distinct().ToArray();
            speedLimits = builtLimits;
            auxiliaryLanes=builtAuxiliary;
            map = nextMap;
            loadedSectors = sectors;
            missingConnection=need; mapIssues=typedIssues.Distinct().ToArray();
        }
        var diagnostics = new List<string>(data.Diagnostics);
        diagnostics.AddRange(data.Companies.Diagnostics);
        diagnostics.AddRange(junctionDiagnostics);
        diagnostics.AddRange(roundaboutIndex.Diagnostics);
        diagnostics.AddRange(speedLimits.Diagnostics);
        if(auxiliaryLanes is not null)diagnostics.AddRange(auxiliaryLanes.Diagnostics);
        if(speedLimits.Error is not null) diagnostics.Add(speedLimits.Error);
        var cameras = new List<CameraObservation>();
        foreach (var item in LoadedMapItems.Enumerate(map.MapItems.Values))
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
                descriptor=data.World.Models.GetValueOrDefault(modelItem.Name.ToString())?.Descriptor;
                scale = modelItem.Scale;
                variant = modelItem.Variant.ToString();
            }
            else if (item is Sign signItem)
            {
                descriptor=data.World.Signs.GetValueOrDefault(signItem.Model.ToString())?.Descriptor;
                variant = signItem.Variant.ToString();
                if (signItem.FollowRoadDir)
                {
                    diagnostics.Add($"Sign {item.Uid:x}: runtime FollowRoadDir placement cannot be reproduced yet.");
                    continue;
                }
            }
            if (descriptor == null) continue;
            if (string.IsNullOrEmpty(variant)) variant = "default";
            try
            {
                var locators = data.GetModelLocators(descriptor);
                if (locators.Count == 0) continue;
                var active = data.GetModelVariant(descriptor, variant);
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
                diagnostics.Add(message);
            }
        }
        return new NearbyMapSnapshot(map, cameras, diagnostics.Distinct().ToArray(), loadedSectors) { ReadySectors=loadedSectors,Coverage=coverage,MissingConnection=missingConnection,MapIssues=mapIssues,Data=data, RoadNames=new RoadNameIndex(map.MapItems.Values), Companies=companies, AuxiliaryLanes=auxiliaryLanes,RoadStructures = roundaboutIndex.RoadAnnotations, Roundabouts = roundaboutIndex.Structures, PrefabPaths = junctionGeometry, SpeedLimits = speedLimits, Ferries=Ferries, FreeNavigationAreas = freeNavigationAreas,TrafficSignals=trafficSignals };
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

    /// <summary>SII attributes are parsed dynamic values, not necessarily source strings.</summary>
    public static (float MeasurementDistance, bool ReverseDirection) ReadDefinitionValues(
        object? measurementDistance, object? reverseDirection)
    {
        return WorldDefinitionIndex.CameraValues(measurementDistance,reverseDirection);
    }

}
