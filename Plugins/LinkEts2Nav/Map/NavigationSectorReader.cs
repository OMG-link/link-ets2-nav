using System.Text;
using TruckLib;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>Reads a bounded group of indexed sectors without attaching their scenery to navigation objects.</summary>
internal sealed class NavigationSectorReader : IDisposable
{
    private readonly string mapPath;
    private readonly BatchFiles subset;
    private readonly CancellationToken token;
    private readonly Dictionary<(Token TrafficRule, Token Variant), RoadSide> roadSides = [];
    private readonly SectorReadEvidence evidence;
    internal NativeMap Result { get; } = new();

    internal NavigationSectorReader(IFileSystem files, string mapPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        this.mapPath = Path.TrimEndingDirectorySeparator(mapPath);
        this.token = token;
        byte[]? header = files.FileExists(this.mapPath) ? files.ReadAllBytes(this.mapPath) : null;
        string directory = header is null ? this.mapPath : files.GetParent(this.mapPath) + files.DirectorySeparator
            + Path.GetFileNameWithoutExtension(this.mapPath);
        subset = new(files, this.mapPath, header, directory);
        Result.Read(this.mapPath, subset); // Empty sector listing, native header only.
        evidence = new();
    }

    internal NativeMap ReadBatch(SectorCoordinate[] sectors, Dictionary<ulong, MapItem> candidates)
    {
        token.ThrowIfCancellationRequested();
        subset.Select(sectors);
        var batch = new Batch(evidence, token, roadSides, candidates);
        try { batch.Read(mapPath, subset); }
        finally { evidence.End(); }
        token.ThrowIfCancellationRequested();
        if (evidence.Failures.Count != 0) throw new MapSectorReadException(evidence.Failures);
        return batch;
    }

    internal void Bind()
    {
        foreach (Node node in Result.Nodes.Values) node.UpdateItemReferences(Result.MapItems);
        foreach (var item in Result.MapItems.Values)
        {
            token.ThrowIfCancellationRequested();
            switch (item)
            {
                case Road road:
                    road.Node = Result.Nodes[road.Node.Uid];
                    road.ForwardNode = Result.Nodes[road.ForwardNode.Uid];
                    break;
                case Prefab prefab:
                    for (int i = 0; i < prefab.Nodes.Count; i++) prefab.Nodes[i] = Result.Nodes[prefab.Nodes[i].Uid];
                    prefab.UpdateItemReferences(Result.MapItems);
                    break;
                case Ferry ferry:
                    ferry.Node = Result.Nodes[ferry.Node.Uid];
                    ferry.UpdateItemReferences(Result.MapItems);
                    break;
            }
        }
    }

    public void Dispose() => evidence.Dispose();

    private sealed class Batch(SectorReadEvidence evidence, CancellationToken token,
        Dictionary<(Token TrafficRule, Token Variant), RoadSide> roadSides, Dictionary<ulong, MapItem> candidates) : NativeMap
    {
        protected override void OnSectorLoading(Sector sector, int index, int total)
        {
            token.ThrowIfCancellationRequested();
            evidence.Begin(sector.Coordinate);
        }

        protected override bool PostProcessItem(MapItem item)
        {
            switch (item)
            {
                case Road road:
                    road.Left = NavigationSide(road.Left);
                    road.Right = NavigationSide(road.Right);
                    road.CenterVegetation = null;
                    road.VegetationSpheres = null;
                    break;
                case Prefab prefab:
                    // Native control points are in Nodes. PrefabNodes describes corner scenery.
                    prefab.PrefabNodes = [];
                    prefab.AdditionalParts = null;
                    prefab.VegetationParts = null;
                    prefab.VegetationSpheres = null;
                    break;
                case Ferry:
                    break;
                default:
                    return false;
            }
            // Keep UID references unresolved until the selected components and their physical
            // endpoints are known. Binding here could keep a whole temporary sector alive.
            candidates.Add(item.Uid, item);
            return false;
        }

        private RoadSide NavigationSide(RoadSide side)
        {
            // The navigation and speed-limit readers use traffic rules and all lane overrides.
            var key = (side.TrafficRule, side.Variant);
            if (side.VariantOverrides.Count == 0 && roadSides.TryGetValue(key, out var other)) return other;
            side.Sidewalk = null;
            side.Terrain = null;
            side.Models = [];
            side.Vegetation = [];
            side.Railings = null;
            side.AdditionalParts = null;
            side.EdgeOverrides = null;
            if (side.VariantOverrides.Count == 0) roadSides.Add(key, side);
            return side;
        }
    }

    private sealed class BatchFiles(IFileSystem source, string mapPath, byte[]? header, string directory) : IFileSystem
    {
        private string[] sectors = [];
        internal void Select(SectorCoordinate[] coordinates) => sectors = coordinates.Select(coordinate =>
            directory + DirectorySeparator + Sector.SectorFileNameFromSectorCoords(coordinate) + ".base").ToArray();
        public char DirectorySeparator => source.DirectorySeparator;
        public IList<string> GetFiles(string path) => path == directory ? sectors : source.GetFiles(path);
        public bool FileExists(string path) => path == mapPath ? header is not null : source.FileExists(path);
        public Stream Open(string path) => path == mapPath && header is not null ? new MemoryStream(header, false) : source.Open(path);
        public string GetParent(string path) => source.GetParent(path);
        public bool DirectoryExists(string path) => source.DirectoryExists(path);
        public byte[] ReadAllBytes(string path) => source.ReadAllBytes(path);
        public string ReadAllText(string path) => source.ReadAllText(path);
        public string ReadAllText(string path, Encoding encoding) => source.ReadAllText(path, encoding);
    }
}
