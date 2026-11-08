using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>Sector lookup of host-owned objects. Never changes ownership or node/item references.</summary>
public sealed class HostMapView
{
    private readonly Dictionary<SectorCoordinate, List<MapItem>> itemsBySector = new();

    public HostMapView(GameMap map)
    {
        foreach (var item in map.MapItems.Values)
        {
            IEnumerable<INode> nodes = item switch
            {
                Road road => new[] { road.Node, road.ForwardNode },
                Prefab prefab => prefab.Nodes,
                SingleNodeItem placed => new[] { placed.Node },
                PolygonItem area => area.Nodes,
                _ => Array.Empty<INode>()
            };
            foreach (var sector in nodes.Where(n => n is not null)
                .Select(n => GameMap.GetSectorOfCoordinate(n.Position)).Distinct())
            {
                if (!itemsBySector.TryGetValue(sector, out var items)) itemsBySector[sector] = items = [];
                items.Add(item);
            }
        }
    }

    public void MergeInto(GameMap supplement, IEnumerable<SectorCoordinate> sectors)
    {
        foreach (var sector in sectors)
            if (itemsBySector.TryGetValue(sector, out var items))
                foreach (var item in items)
                {
                    supplement.MapItems[item.Uid] = item;
                    IEnumerable<INode> nodes = item switch
                    {
                        Road road => new[] { road.Node, road.ForwardNode },
                        Prefab prefab => prefab.Nodes,
                        SingleNodeItem placed => new[] { placed.Node },
                PolygonItem area => area.Nodes,
                        _ => Array.Empty<INode>()
                    };
                    foreach (var node in nodes.Where(n => n is not null))
                        if (supplement.Nodes.ContainsKey(node.Uid)) supplement.Nodes[node.Uid] = node;
                        else supplement.Nodes.Add(node.Uid, node);
                }
    }
}
