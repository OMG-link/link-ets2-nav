using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TruckLib;
using PrefabDescriptor=TruckLib.Models.Ppd.PrefabDescriptor;
using TruckLib.ScsMap;
using TruckLib.Sii;
using NativeMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

public sealed record GameDataStatistics(long ResourceReads,long DirectoryScans,long SiiParses,long ResidentResourceBytes);
public sealed record PrefabData(Prefab Instance,GameResourceDefinition? Definition,Func<PrefabDescriptor?> ReadDescription)
{
    public PrefabDescriptor? Description=>ReadDescription();
}

/// <summary>One mounted dataset. All readers share definitions, binary resources and native-object queries.</summary>
public sealed class GameDataManager:IDisposable
{
    private sealed class Scope { public readonly Dictionary<string,GameDataManager> Managers=new(StringComparer.Ordinal); }
    private static readonly ConditionalWeakTable<IFileSystem,Scope> defaults=new();
    private readonly GameResourceStore resources;
    private readonly ConcurrentDictionary<string,Lazy<GameSiiDocument>> documents=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Kind,string Resource),Lazy<object>> projections=new();
    private readonly Lazy<WorldDefinitionIndex> world;
    private readonly object preparation=new();
    private readonly IDisposable? owner;
    private readonly string mapPath;
    private bool prepared;
    private long parses;
    private SpeedLimitDefinitionReader? speedLimits;
    private CompanyPlaceReader? companies;
    private FerryJourneyIndex? ferries;
    private RouteJourneyEstimator? estimator;
    private NativeMap? loadedWindow;
    private SectorCoordinate[] loadedSectors=[];
    private readonly ConditionalWeakTable<NativeMap,MapWindowReadiness> windowReadiness=new();
    private readonly IReadOnlyList<string> mods;
    internal IFileSystem Resources=>resources;
    public GameMapData Map {get;}
    public WorldDefinitionIndex World=>world.Value;
    public IReadOnlyList<string> Diagnostics=>Array.Empty<string>();
    public GameDataStatistics Statistics=>new(resources.ResourceReads,resources.DirectoryScans,Interlocked.Read(ref parses),resources.ResidentBytes);
    public SpeedLimitDefinitionReader SpeedLimits=>speedLimits??throw new InvalidOperationException("Dataset has not been prepared.");
    public CompanyPlaceReader Companies=>companies??throw new InvalidOperationException("Dataset has not been prepared.");
    public FerryJourneyIndex? Ferries=>ferries;
    public RouteJourneyEstimator? JourneyEstimator=>estimator;

    public GameDataManager(IFileSystem source,IReadOnlyList<string>? activeMods=null,NativeMap? map=null,string mapPath="/map/europe.mbd")
        :this(source,activeMods,map,mapPath,null) { }
    private GameDataManager(IFileSystem source,IReadOnlyList<string>? activeMods,NativeMap? map,string mapPath,IDisposable? owner)
    {
        mods=activeMods?.ToArray()??[];this.mapPath=mapPath;this.owner=owner;
        resources=new(this,()=>GameDefinitionFileSystem.Create(source,mods),mapPath);
        Map=new(map);world=new(()=>new WorldDefinitionIndex(this),LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public static GameDataManager Open(string gamePath,IEnumerable<string> logLines,NativeMap? map=null)
    {
        var mount=OfficialArchiveMount.FromGameLog(gamePath,logLines);
        bool transferred=false;
        try
        {
            var manager=new GameDataManager(mount.FileSystem,mount.ActiveModPaths,map,"/map/europe.mbd",mount);
            transferred=true;
            return manager;
        }
        finally { if(!transferred)mount.Dispose(); }
    }
    // Also makes standalone readers and tools using the same filesystem share one dataset.
    public static GameDataManager From(IFileSystem source,IReadOnlyList<string>? mods=null)
    {
        if(source is GameResourceStore managed)
        {
            if(mods is {Count:>0}&&!managed.Owner.mods.SequenceEqual(mods))throw new ArgumentException("Use a new dataset for a different mod selection.",nameof(mods));
            return managed.Owner;
        }
        var scope=defaults.GetValue(source,_=>new());string key=string.Join('\0',mods??[]);
        lock(scope.Managers)
        {
            if(!scope.Managers.TryGetValue(key,out var data))scope.Managers[key]=data=new(source,mods);
            return data;
        }
    }
    public GameSiiDocument GetSii(string path)=>documents.GetOrAdd(GameResourceStore.Key(path),p=>new(()=>
    {
        Interlocked.Increment(ref parses);return new(p,SiiFile.Open(p,resources));
    },LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    public IReadOnlyList<string> GetFiles(string path)=>Array.AsReadOnly(resources.GetFiles(path).ToArray());
    public bool FileExists(string path)=>resources.FileExists(path);
    public bool DirectoryExists(string path)=>resources.DirectoryExists(path);
    public string GetParent(string path)=>resources.GetParent(path);
    public ReadOnlyMemory<byte> GetResource(string path)=>resources.Read(path);
    // Keep the catalogue-reader API while propagating invalid data and reader
    // failures. Neither parsing nor projection has a local recovery strategy.
    internal void TryDefinitions(string path,Action<GameSiiDocument> action)=>action(GetSii(path));
    internal T Projection<T>(string kind,string key,Func<T> factory) where T:notnull
        =>(T)projections.GetOrAdd((kind,key),_=>new(()=>factory(),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    internal IReadOnlyDictionary<string,string> SignIndex(string prefix)=>Projection("sign-index",prefix,()=>
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in GetFiles("/def/sign/").Where(p=>GetParent(p)=="/def/sign"&&Path.GetFileName(p).StartsWith(prefix,StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            TryDefinitions(path,document=>
            {foreach(var unit in document.Units)
                if(unit.Attributes.ContainsKey("path"))result[unit.Name]=WorldDefinitionIndex.Text(unit,"path");});
        return (IReadOnlyDictionary<string,string>)new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(result);
    });
    public PrefabData? GetPrefab(ulong uid)
    {
        var prefab=Map.GetItem<Prefab>(uid);if(prefab is null)return null;
        var definition=World.Prefabs.GetValueOrDefault(prefab.Model.ToString());
        return new(prefab,definition,()=>GetPrefabNavigationDescription(prefab,definition?.Descriptor));
    }
    /// <summary>Effective passage lengths after the normal player-permission projection.</summary>
    public IReadOnlyList<double> GetPrefabPassageLengths(ulong prefabUid,ulong entry,ulong exit)
    {
        var prefab=GetPrefab(prefabUid);
        if(prefab?.Description is not {} description)return [];
        SpeedLimitDefinitionReader definitions;
        lock(preparation)definitions=speedLimits??=new(this);
        var projected=PrefabPlayerAccessWindow.Build([new(prefab.Instance,description)],definitions,Map)[prefabUid];
        return Projection("prefab-passage-lengths",$"{prefabUid:X16}:{entry:X16}:{exit:X16}:{projected.CacheKey}",
            ()=>PrefabPassageLengths.Read(prefab.Instance,projected.Descriptor,entry,exit));
    }
    public PrefabDescriptor GetPrefabDescription(string path)
    {
        string resource = GameResourceStore.Key(path);
        return Projection("ppd", resource, () =>
        {
            var descriptor = PrefabDescriptor.Open(resource, resources);
            PrefabDescriptorInput.Validate(resource, descriptor);
            return descriptor;
        });
    }
    /// <summary>The effective navigation descriptor for a particular loaded instance.</summary>
    internal PrefabDescriptor? GetPrefabNavigationDescription(Prefab prefab,string? resource)
    {
        if(PrefabNavigationOverrides.Find(prefab.Uid) is not {} correction)
            return resource is null ? null : ReadForInstance(resource);
        correction.ValidateInstance(prefab,resource);
        var native = ReadForInstance(correction.Resource);
        return Projection("prefab-navigation-instance",prefab.Uid.ToString("X16"),
            ()=>correction.Apply(prefab,native));

        PrefabDescriptor ReadForInstance(string resourcePath)
        {
            var descriptor = GetPrefabDescription(resourcePath);
            PrefabDescriptorInput.ValidateInstance(resourcePath, prefab, descriptor);
            return descriptor;
        }
    }
    public TruckLib.Models.Model GetModelDescription(string path)=>Projection("model-description",GameResourceStore.Key(path),()=>TruckLib.Models.Model.Open(path,resources));
    /// <summary>All named model anchors and hookups, before instance variant visibility is applied.</summary>
    public IReadOnlyList<ModelLocator> GetNamedModelLocators(string descriptor)=>Projection("named-model-locators",GameResourceStore.Key(descriptor),()=>
    {using var stream=resources.Open(Path.ChangeExtension(descriptor,".pmg"));return Array.AsReadOnly(PmgLocatorReader.ReadAll(stream).ToArray());});
    public IReadOnlyList<SpeedCameraLocator> GetModelLocators(string descriptor)=>Projection("camera-locators",GameResourceStore.Key(descriptor),()=>
    {using var stream=resources.Open(Path.ChangeExtension(descriptor,".pmg"));return PmgLocatorReader.Read(stream,World.Cameras);});
    public (IReadOnlySet<int> Visible,string Fingerprint) GetModelVariant(string descriptor,string variant)
    {
        var metadata=Projection("pmd",GameResourceStore.Key(descriptor),()=>
        {
            byte[] bytes=resources.ReadAllBytes(descriptor);
            if(bytes.Length>16*1024*1024)throw new InvalidDataException("Unsupported PMD metadata stream.");
            return (Bytes:bytes,Hash:Convert.ToHexString(SHA256.HashData(bytes)));
        });
        return Projection("pmd-variant",GameResourceStore.Key(descriptor)+"\0"+variant,()=>
            (PmdVariantReader.ReadVisibleParts(metadata.Bytes,variant),metadata.Hash));
    }
    public void Prepare(CancellationToken token=default)
    {
        lock(preparation)
        {
            if(prepared)return;
            token.ThrowIfCancellationRequested();
            // Fixed catalogues are prepared before the first position-dependent scan.
            _=World;
            speedLimits??=new SpeedLimitDefinitionReader(this);
            _=SignIndex("templates");_=SignIndex("boards");
            companies??=new CompanyPlaceReader(this);_=companies.ReadNames();
            speedLimits.PrepareHost(Map.NativeBase,token);
            if(Map.NativeBase is not null)
            {
                ferries??=FerryJourneyIndex.Load(this,token:token,roadHalfWidth:speedLimits.ReferenceHalfWidth);
                estimator??=new(Map,speedLimits.RoadCategoryLimit,JourneyEstimationCalibration.PrefabDistanceFactor,ferries,(p,a,b)=>GetPrefabPassageLengths(p.Uid,a,b));
            }
            token.ThrowIfCancellationRequested();
            prepared=true;
        }
    }
    internal SpeedLimitData ReadSpeedLimits(NativeMap window,IReadOnlyList<LimitRegion> regions,CancellationToken token)
        =>SpeedLimits.Build(window,token,Map.NativeBase,regions);
    internal NativeMap ReadWindow(IEnumerable<SectorCoordinate> requested)
    {
        var sectors=OrderedSectors(requested);
        if(loadedWindow is not null&&sectors.SequenceEqual(loadedSectors))return loadedWindow;
        var map=ReadSupplement(sectors,regionsOnly:false);
        // Capture before merging host metadata: absent files are confirmed empty, not loaded.
        var existing=map.Sectors.Keys.ToHashSet();
        Map.MergeInto(map,sectors);
        // TruckLib reserves slots for every serialized item, including the scenery filtered during the read.
        map.MapItems.TrimExcess();
        Map.Publish(map);
        windowReadiness.Add(map,new(sectors.Where(existing.Contains).ToArray(),sectors.Where(s=>!existing.Contains(s)).ToArray()));
        loadedWindow=map;loadedSectors=sectors;return map;
    }

    internal IReadOnlyList<LimitRegion> ReadLimitRegions(IEnumerable<SectorCoordinate> requested)
    {
        var sectors=OrderedSectors(requested);
        var map=ReadSupplement(sectors,regionsOnly:true);
        Map.MergeInto(map,sectors);
        // The region values contain their own positions and rules. The temporary map
        // has no route evidence to publish and can be released after projection.
        return SpeedLimits.ReadRegions(map);
    }

    private static SectorCoordinate[] OrderedSectors(IEnumerable<SectorCoordinate> requested)
        =>requested.Distinct().OrderBy(s=>s.X).ThenBy(s=>s.Z).ToArray();

    private NativeMap ReadSupplement(SectorCoordinate[] sectors,bool regionsOnly)
    {
        using var evidence=new SectorReadEvidence();
        var map=new SupplementMap(Map.NativeBase,evidence,regionsOnly);
        try{map.Read(mapPath,resources,sectors);}finally{evidence.End();}
        if(evidence.Failures.Count>0)throw new MapSectorReadException(evidence.Failures);
        return map;
    }

    internal MapWindowReadiness WindowReadiness(NativeMap window)=>windowReadiness.TryGetValue(window,out var value)?value:throw new InvalidOperationException("Unknown map read window.");
    public void Dispose()=>owner?.Dispose();
    internal static MapQueryIssue DescribeMissingNativeObjects(NativeMap window,ulong boundaryUid)
    {
        if(!window.Nodes.TryGetValue(boundaryUid,out var raw)||raw is not Node node)
            return new MissingMapObjects([new(MapObjectKind.Node,boundaryUid)],$"Native node {boundaryUid:X16} is not in the current window.");
        var missing=new HashSet<MapObjectKey>();
        foreach(var reference in new[]{node.BackwardItem,node.ForwardItem}.Where(i=>i is not null).DistinctBy(i=>i.Uid))
        {
            if(!window.MapItems.TryGetValue(reference.Uid,out var item))
            {missing.Add(new(MapObjectKind.Item,reference.Uid));continue;}
            foreach(var endpoint in HostMapView.NodesOf(item))
                if(!window.Nodes.TryGetValue(endpoint.Uid,out var actual)||actual is not Node)
                    missing.Add(new(MapObjectKind.Node,endpoint.Uid));
        }
        if(missing.Count>0)return new MissingMapObjects(missing,$"Boundary {boundaryUid:X16} needs missing native lookup keys."){QueryAnchor=node.Position};
        return new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,$"Boundary {boundaryUid:X16} has no verified connection in the complete current window."){QueryAnchor=node.Position};
    }
    public MapQueryIssue DescribeMissingConnection(NativeMap window,ulong boundaryUid)
    {
        var result=DescribeMissingNativeObjects(window,boundaryUid);
        if(result is MissingMapObjects)return result;
        var node=(Node)window.Nodes[boundaryUid];
        var prefabs=new[]{node.BackwardItem,node.ForwardItem}.Where(i=>i is not null).Select(i=>i.Uid)
            .Where(uid=>window.MapItems.GetValueOrDefault(uid) is Prefab).Distinct();
        var details=new List<string>();
        foreach(var uid in prefabs)
        {
            var prefab=GetPrefab(uid);
            if(prefab?.Definition is not {} definition)continue;
            if(!FileExists(definition.Descriptor))
            {details.Add($"Prefab {uid:X16} definition {definition.Descriptor} is missing.");continue;}
            if(prefab.Description is {} description&&description.NavCurves.Count==0)
                details.Add($"Prefab {uid:X16} ({definition.Descriptor}) has no native navigation curves (NavCurves=0; SpawnPoints={description.SpawnPoints.Count}).");
        }
        return new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,
            $"Boundary {boundaryUid:X16} has no verified connection in the complete current window."
            +(details.Count==0?"":" "+string.Join(' ',details))){QueryAnchor=node.Position};
    }

    /// <summary>Validate a requested SDK passage only when both map interfaces and their owning items are loaded.</summary>
    internal void ValidateMissingPrefabPassage(NativeMap window,CompanyPlaceIndex companies,Node entry,Node exit)
    {
        var owners=new[]{exit.BackwardItem?.Uid,exit.ForwardItem?.Uid};
        var shared=new[]{entry.BackwardItem,entry.ForwardItem}.OfType<IMapObject>()
            .Where(item=>owners.Contains(item.Uid)).Select(item=>item.Uid).Distinct();
        foreach(ulong uid in shared)
        {
            if(window.MapItems.GetValueOrDefault(uid) is not Prefab prefab)continue;
            if(prefab.Ferry is not null||Ferries?.Ports.Any(port=>port.PrefabUid==uid)==true
                ||prefab.SlaveItems.Any(item=>item is Company)
                ||companies.Places.Any(company=>company.PrefabUid==uid))continue;
            var source=GetPrefab(uid);
            var resource=source?.Definition?.Descriptor;
            var description=resource is not null&&FileExists(resource)?source!.Description:null;
            // An unfiltered path can be absent from the player graph because of access rules.
            if(description is not null&&PrefabLaneTopology.Reaches(prefab,description,entry.Uid,exit.Uid))continue;
            var definition=resource is null?$"model {prefab.Model}":resource;
            throw new InvalidDataException($"Prefab {uid:X16} ({definition}) has no native lane path for SDK passage {entry.Uid:X16} → {exit.Uid:X16}.");
        }
    }

    private sealed class SupplementMap(NativeMap? basis,SectorReadEvidence evidence,bool regionsOnly):NativeMap
    {
        protected override void OnSectorLoading(Sector sector,int current,int total)
        {evidence.Begin(sector.Coordinate);base.OnSectorLoading(sector,current,total);}
        protected override bool PostProcessItem(MapItem item)
        {
            if(basis?.MapItems.ContainsKey(item.Uid)==true)return false;
            if(regionsOnly)return item is CityArea or TrafficArea;
            // TruckLib calls this after the entire native data payload is read.
            switch(item)
            {
                case Road road:
                    road.Left.Terrain=null;road.Right.Terrain=null;
                    return true;
                case Prefab prefab:
                    foreach(var node in prefab.PrefabNodes)node.Terrain=null;
                    return true;
                case TruckLib.ScsMap.Model model:
                    return model.ModelHookups;
                case Compound compound:
                    return LoadedMapItems.RetainSupplementSigns(compound);
                default:
                    return item is Sign or Company or CityArea or TrafficArea;
            }
        }
    }
}

internal sealed record MapWindowReadiness(IReadOnlyList<SectorCoordinate> Loaded,IReadOnlyList<SectorCoordinate> Empty);
