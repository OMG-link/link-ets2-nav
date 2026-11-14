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
    private readonly ConcurrentDictionary<string,string> diagnostics=new(StringComparer.Ordinal);
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
    private readonly IReadOnlyList<string> mods;
    internal IFileSystem Resources=>resources;
    public GameMapData Map {get;}
    public WorldDefinitionIndex World=>world.Value;
    public IReadOnlyList<string> Diagnostics=>diagnostics.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Value).ToArray();
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
        resources=new(this,()=>GameDefinitionFileSystem.Create(source,mods));
        Map=new(map);world=new(()=>new WorldDefinitionIndex(this),LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public static GameDataManager Open(string gamePath,IEnumerable<string> logLines,NativeMap? map=null)
    {
        var mount=OfficialArchiveMount.FromGameLog(gamePath,logLines);
        try{return new(mount.FileSystem,mount.ActiveModPaths,map,"/map/europe.mbd",mount);}
        catch{mount.Dispose();throw;}
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
    internal void TryDefinitions(string path,Action<GameSiiDocument> action)
    {
        try{action(GetSii(path));}
        catch(Exception ex) when(ex is not OperationCanceledException and not OutOfMemoryException)
        {diagnostics.TryAdd(path,path+": "+ex.GetType().Name+": "+ex.Message);}
    }
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
        return new(prefab,definition,()=>definition is null?null:GetPrefabDescription(definition.Descriptor));
    }
    public PrefabDescriptor GetPrefabDescription(string path)=>Projection("ppd",GameResourceStore.Key(path),()=>PrefabDescriptor.Open(path,resources));
    public TruckLib.Models.Model GetModelDescription(string path)=>Projection("model-description",GameResourceStore.Key(path),()=>TruckLib.Models.Model.Open(path,resources));
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
                estimator??=new(Map,speedLimits.RoadCategoryLimit,JourneyEstimationCalibration.PrefabDistanceFactor,ferries);
            }
            token.ThrowIfCancellationRequested();
            prepared=true;
        }
    }
    internal SpeedLimitData ReadSpeedLimits(NativeMap window,IReadOnlyList<LimitRegion> regions,CancellationToken token)
        =>SpeedLimits.Build(window,token,Map.NativeBase,regions);
    internal NativeMap ReadWindow(IEnumerable<SectorCoordinate> requested)
    {
        var sectors=requested.Distinct().OrderBy(s=>s.X).ThenBy(s=>s.Z).ToArray();
        if(loadedWindow is not null&&sectors.SequenceEqual(loadedSectors))return loadedWindow;
        var map=new SupplementMap(Map.NativeBase);map.Read(mapPath,resources,sectors);Map.MergeInto(map,sectors);
        loadedWindow=map;loadedSectors=sectors;Map.Publish(map);return map;
    }
    public void Dispose()=>owner?.Dispose();
    private sealed class SupplementMap(NativeMap? basis):NativeMap
    {
        protected override bool PostProcessItem(MapItem item)
        {
            if(basis?.MapItems.ContainsKey(item.Uid)==true)return false;
            if(item is Compound compound)return LoadedMapItems.RetainSupplementSigns(compound);
            return item is TruckLib.ScsMap.Model or Sign or Road or Prefab or Company or CityArea or TrafficArea;
        }
    }
}
