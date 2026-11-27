using System.Text;
using TruckLib;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>The standalone host's read-only national road, port and city map.</summary>
internal static class NavigationBaseMap
{
    private const int SectorsPerBatch = 4;

    internal static NativeMap Read(IFileSystem files, string mapPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        mapPath = Path.TrimEndingDirectorySeparator(mapPath);
        byte[]? header = files.FileExists(mapPath) ? files.ReadAllBytes(mapPath) : null;
        string directory = header is null ? mapPath : files.GetParent(mapPath) + files.DirectorySeparator
            + Path.GetFileNameWithoutExtension(mapPath);
        var batches = files.GetFiles(directory).Where(f => Path.GetExtension(f) == ".base").Chunk(SectorsPerBatch);
        var result = new NativeMap();
        var subset = new BatchFiles(files, mapPath, header, directory);
        result.Read(mapPath, subset); // Header only. Each sector will be parsed by TruckLib below.
        var possibleBoundaryNodes = new Dictionary<ulong, Node>();
        var omitted = new List<(string[] Files, UnresolvedItemReferenceCounts Nodes)>();
        var roadSides = new Dictionary<(Token TrafficRule, Token Variant), RoadSide>();
        using var evidence = new SectorReadEvidence();

        foreach (var paths in batches)
        {
            var batch = ReadBatch(paths);
            foreach (var (coordinate, sector) in batch.Sectors)
            {
                sector.Map = result;
                result.Sectors.Add(coordinate, sector);
            }
            foreach (var item in batch.MapItems.Values) result.MapItems.Add(item.Uid, item);
            var endpoints = batch.MapItems.Values.SelectMany(HostMapView.NodesOf).Select(n => n.Uid).ToHashSet();
            var skipped = new List<ulong>();
            foreach (Node node in batch.Nodes.Values)
            {
                if (endpoints.Contains(node.Uid) || HasRetainedOwner(node)) AddNode(node);
                else if (MayReferOutside(node.BackwardItem) || MayReferOutside(node.ForwardItem))
                {
                    node.Parent = result;
                    if (result.Nodes.TryGetValue(node.Uid, out var known))
                        NativeNodeConnections.ValidateRepeatedNode((Node)known, node);
                    else if (!possibleBoundaryNodes.TryAdd(node.Uid, node))
                        NativeNodeConnections.ValidateRepeatedNode(possibleBoundaryNodes[node.Uid], node);
                }
                else skipped.Add(node.Uid);
            }
            if (skipped.Count > 0) omitted.Add((paths, UnresolvedItemReferenceCounts.Build(skipped)));

            bool MayReferOutside(IMapObject? owner) => owner is not null && !batch.Discarded.Contains(owner.Uid);
        }

        var missing = result.MapItems.Values.SelectMany(HostMapView.NodesOf).Select(n => n.Uid)
            .Where(uid => !result.Nodes.ContainsKey(uid)).ToHashSet();
        foreach (var node in possibleBoundaryNodes.Values)
            if (missing.Remove(node.Uid) || HasRetainedOwner(node)) AddNode(node);

        // An item may refer to a physical node stored with a different sector's scenery.
        // The exact UID directory lets us recover that row without loading unrelated sectors.
        foreach (var archive in omitted)
        {
            if (missing.Count == 0) break;
            var wanted = missing.Where(uid => archive.Nodes.Count(uid) > 0).ToArray();
            if (wanted.Length == 0) continue;
            var batch = ReadBatch(archive.Files);
            foreach (ulong uid in wanted)
            {
                AddNode((Node)batch.Nodes[uid]);
                missing.Remove(uid);
            }
        }

        foreach (Node node in result.Nodes.Values) node.UpdateItemReferences(result.MapItems);
        foreach (var item in result.MapItems.Values)
        {
            token.ThrowIfCancellationRequested();
            switch (item)
            {
                case Road road:
                    road.Node = Resolve(road.Node);
                    road.ForwardNode = Resolve(road.ForwardNode);
                    break;
                case Prefab prefab:
                    for (int i = 0; i < prefab.Nodes.Count; i++) prefab.Nodes[i] = Resolve(prefab.Nodes[i]);
                    prefab.UpdateItemReferences(result.MapItems);
                    break;
                case Ferry ferry:
                    ferry.Node = Resolve(ferry.Node);
                    ferry.UpdateItemReferences(result.MapItems);
                    break;
                case CityArea city:
                    city.Node = Resolve(city.Node);
                    break;
            }
        }
        return result;

        Batch ReadBatch(string[] paths)
        {
            token.ThrowIfCancellationRequested();
            subset.Sectors = paths;
            var batch = new Batch(evidence, token, roadSides);
            try { batch.Read(mapPath, subset); }
            finally { evidence.End(); }
            token.ThrowIfCancellationRequested();
            if (evidence.Failures.Count != 0) throw new MapSectorReadException(evidence.Failures);
            return batch;
        }
        bool HasRetainedOwner(Node node) => node.BackwardItem is { } back && result.MapItems.ContainsKey(back.Uid)
            || node.ForwardItem is { } forward && result.MapItems.ContainsKey(forward.Uid);
        INode Resolve(INode reference) => result.Nodes.TryGetValue(reference.Uid, out var node) ? node : reference;
        void AddNode(Node node)
        {
            // A retained item may still point at this duplicate until all sectors have been linked.
            node.Parent = result;
            if (result.Nodes.TryGetValue(node.Uid, out var existing))
                NativeNodeConnections.ValidateRepeatedNode((Node)existing, node);
            else
            {
                if (possibleBoundaryNodes.TryGetValue(node.Uid, out var boundary) && !ReferenceEquals(boundary, node))
                    NativeNodeConnections.ValidateRepeatedNode(boundary, node);
                result.Nodes.Add(node.Uid, node);
            }
        }
    }

    private sealed class Batch(SectorReadEvidence evidence, CancellationToken token,
        Dictionary<(Token TrafficRule, Token Variant), RoadSide> roadSides) : NativeMap
    {
        internal readonly HashSet<ulong> Discarded = [];
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
                    return true;
                case Prefab prefab:
                    // The native control points are in Nodes. PrefabNodes contains corner scenery.
                    prefab.PrefabNodes = [];
                    prefab.AdditionalParts = null;
                    prefab.VegetationParts = null;
                    prefab.VegetationSpheres = null;
                    return true;
                case Ferry or CityArea:
                    return true;
                default:
                    Discarded.Add(item.Uid);
                    return false;
            }
        }
        private RoadSide NavigationSide(RoadSide side)
        {
            // These read-only sides supply precisely the values used by AnchoredRoadLanes and
            // SpeedLimitDefinitionReader. Preserve every section of a side with lane overrides.
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

    // Map.Read takes a directory listing even for selected sectors. Keep its native file order
    // and header semantics while mounting/decompressing the shared header only once.
    private sealed class BatchFiles(IFileSystem source, string mapPath, byte[]? header, string directory) : IFileSystem
    {
        internal string[] Sectors = [];
        public char DirectorySeparator => source.DirectorySeparator;
        public IList<string> GetFiles(string path) => path == directory ? Sectors : source.GetFiles(path);
        public bool FileExists(string path) => path == mapPath ? header is not null : source.FileExists(path);
        public Stream Open(string path) => path == mapPath && header is not null ? new MemoryStream(header, false) : source.Open(path);
        public string GetParent(string path) => source.GetParent(path);
        public bool DirectoryExists(string path) => source.DirectoryExists(path);
        public byte[] ReadAllBytes(string path) => source.ReadAllBytes(path);
        public string ReadAllText(string path) => source.ReadAllText(path);
        public string ReadAllText(string path, Encoding encoding) => source.ReadAllText(path, encoding);
    }
}
