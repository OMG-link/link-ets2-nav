using LinkEts2Nav.Runtime;
using TruckLib;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>Materializes the native geometry required by one SDK route or by the port directory.</summary>
internal static class NavigationRouteMap
{
    private const int SectorsPerBatch = 4;

    internal static NativeMap ReadRoute(IFileSystem files, string mapPath, NavigationSectorIndex index,
        RouteSnapshot route, CancellationToken token)
    {
        using var reader = new NavigationSectorReader(files, mapPath, token);
        var selection = new Selection(reader, index, token);
        HashSet<ulong>? ports = null;
        foreach (ulong uid in route.Nodes.Select(n => n.Uid).Distinct())
        {
            token.ThrowIfCancellationRequested();
            // The SDK may use a ferry activation item UID in place of its physical node.
            if (!selection.RequestNode(uid, Need.Component) && (ports ??= index.FerryComponents.ToHashSet()).Contains(uid))
                selection.RequestItem(uid, Need.Component);
        }
        return selection.Read();
    }

    internal static NativeMap ReadFerries(IFileSystem files, string mapPath, NavigationSectorIndex index,
        CancellationToken token)
    {
        using var reader = new NavigationSectorReader(files, mapPath, token);
        var selection = new Selection(reader, index, token);
        foreach (ulong uid in index.FerryComponents) selection.RequestItem(uid, Need.Component);
        return selection.Read();
    }

    private enum Need { Node, Component, AdjacentRoad }

    private sealed class Selection(NavigationSectorReader reader, NavigationSectorIndex index,
        CancellationToken token)
    {
        private readonly Dictionary<SectorCoordinate, List<(ulong Uid, Need Kind)>> pending = [];
        private readonly Dictionary<ulong, NavigationNodeLocation> nodes = [];
        private readonly HashSet<ulong> items = [];

        internal bool RequestNode(ulong uid, Need owners)
        {
            if (!nodes.TryGetValue(uid, out var location))
            {
                if (!index.TryGetNode(uid, out location)) return false;
                nodes.Add(uid, location);
                Enqueue(location.Sector, uid, Need.Node);
            }
            if (owners != Need.Node)
            {
                RequestItem(location.BackwardItemUid, owners);
                RequestItem(location.ForwardItemUid, owners);
            }
            return true;
        }

        internal void RequestItem(ulong uid, Need kind)
        {
            // All primary components are requested before the first adjacency is explored.
            if (uid != 0 && items.Add(uid) && index.TryGetItem(uid, out var sector))
                Enqueue(sector, uid, kind);
        }

        private void Enqueue(SectorCoordinate sector, ulong uid, Need kind)
        {
            if (!pending.TryGetValue(sector, out var requests)) pending.Add(sector, requests = []);
            requests.Add((uid, kind));
        }

        internal NativeMap Read()
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var sectors = pending.Keys.Take(SectorsPerBatch).ToArray();
                ReadBatch(sectors);
            }
            reader.Bind();
            return reader.Result;
        }

        private void ReadBatch(SectorCoordinate[] sectors)
        {
            var candidates = new Dictionary<ulong, MapItem>();
            var batch = reader.ReadBatch(sectors, candidates);
            foreach (var (coordinate, sector) in batch.Sectors)
            {
                sector.Map = reader.Result;
                reader.Result.Sectors.TryAdd(coordinate, sector);
            }

            // Discover same-sector owners and endpoints while this batch is still in memory.
            // A cross-sector declaration queues its exact archive sector for a subsequent batch.
            bool processed;
            do
            {
                processed = false;
                foreach (var sector in sectors)
                {
                    if (!pending.Remove(sector, out var requests)) continue;
                    processed = true;
                    foreach (var (uid, kind) in requests)
                    {
                        token.ThrowIfCancellationRequested();
                        if (kind == Need.Node)
                        {
                            if (!batch.Nodes.TryGetValue(uid, out var node) || node is not Node physical)
                                throw MissingIndexedObject(uid, sector);
                            physical.Parent = reader.Result;
                            reader.Result.Nodes.Add(uid, physical);
                            continue;
                        }

                        if (!candidates.TryGetValue(uid, out var item)) throw MissingIndexedObject(uid, sector);
                        if (kind == Need.AdjacentRoad && item is not Road) continue;
                        reader.Result.MapItems.Add(uid, item);
                        var owners = kind == Need.Component ? Need.AdjacentRoad : Need.Node;
                        foreach (var endpoint in HostMapView.NodesOf(item))
                            if (!RequestNode(endpoint.Uid, owners))
                                throw new InvalidDataException($"Physical node {endpoint.Uid:X16} of {item.Uid:X16} is missing from the map.");
                    }
                }
            } while (processed);
        }

        private static InvalidDataException MissingIndexedObject(ulong uid, SectorCoordinate sector)
            => new($"Indexed map object {uid:X16} is missing from sector {sector}.");
    }
}
