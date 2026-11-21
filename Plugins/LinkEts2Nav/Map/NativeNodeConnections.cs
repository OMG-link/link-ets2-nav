using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

internal sealed class NativeNodeConnections
{
    private readonly record struct NodeReference(ulong NodeUid, ulong ItemUid);
    private HashSet<NodeReference> pendingNodes = [];

    /// <summary>Requires a native node and a component that owns one of its sides.</summary>
    public static MapQueryResult<MapItem?> Other(Node node, MapItem component, Func<ulong, MapItem?> getItem)
    {
#if DEBUG
        if (node.BackwardItem?.Uid != component.Uid && node.ForwardItem?.Uid != component.Uid)
            throw new NavigationContractException($"Node {node.Uid:X16} is not connected to component {component.Uid:X16}.");
#endif
        var reference = node.BackwardItem?.Uid == component.Uid ? node.ForwardItem : node.BackwardItem;
        if (reference is null)
            return MapQueryResult<MapItem?>.Ready(null);
        if (getItem(reference.Uid) is { } other)
            return MapQueryResult<MapItem?>.Ready(other);
        return MapQueryResult<MapItem?>.Unavailable(new MissingMapObjects([new(MapObjectKind.Item, reference.Uid)],
            $"Node {node.Uid:X16} is connected to item {reference.Uid:X16}, which is not loaded.") { QueryAnchor = node.Position });
    }

    /// <summary>Checks both declarations of a connection when both native objects are visible.</summary>
    public void Validate(IEnumerable<INode> nodes, IEnumerable<MapItem> items,
        Func<ulong, INode?> getNode, Func<ulong, MapItem?> getItem)
    {
        var nextNodes = new HashSet<NodeReference>();
        foreach (var reference in pendingNodes)
            CheckOwner(reference.NodeUid, reference.ItemUid);

        foreach (var item in items)
        {
            switch (item)
            {
                case Road road:
                    CheckOwnerEndpoint(road, road.Node);
                    CheckOwnerEndpoint(road, road.ForwardNode);
                    break;
                case Prefab prefab:
                    foreach (var endpoint in prefab.Nodes)
                        CheckOwnerEndpoint(prefab, endpoint);
                    break;
            }
        }

        ValidateReferences(nodes, getItem);
        pendingNodes = nextNodes;

        void CheckOwnerEndpoint(MapItem owner, INode? endpoint)
        {
            if (endpoint is null)
                throw new InvalidDataException($"Native component {owner.Uid:X16} has a null interface node.");
            CheckOwner(endpoint.Uid, owner.Uid);
        }

        void CheckOwner(ulong nodeUid, ulong itemUid)
        {
            if (getNode(nodeUid) is not Node node)
            {
                nextNodes.Add(new(nodeUid, itemUid));
                return;
            }
            bool backward = node.BackwardItem?.Uid == itemUid;
            bool forward = node.ForwardItem?.Uid == itemUid;
            if (backward == forward)
                throw new InvalidDataException(
                    $"Native node {node.Uid:X16} must reference component {itemUid:X16} on exactly one side. " +
                    $"Backward item: {node.BackwardItem?.Uid:X16}, forward item: {node.ForwardItem?.Uid:X16}.");
        }

    }

    /// <summary>Checks node declarations against the navigation items that have just become available.</summary>
    public static void ValidateReferences(IEnumerable<INode> nodes, Func<ulong, MapItem?> getItem)
    {
        foreach (var raw in nodes)
        {
            if (raw is not Node node)
                continue;
            if (node.BackwardItem is { } backward)
                CheckReference(node.Uid, backward.Uid);
            if (node.ForwardItem is { } forward)
                CheckReference(node.Uid, forward.Uid);
        }

        void CheckReference(ulong nodeUid, ulong itemUid)
        {
            var item = getItem(itemUid);
            bool mismatched = item switch
            {
                Road road => road.Node.Uid != nodeUid && road.ForwardNode.Uid != nodeUid,
                Prefab prefab => prefab.Nodes.All(endpoint => endpoint.Uid != nodeUid),
                _ => false
            };
            if (mismatched)
                throw new InvalidDataException($"Native component {itemUid:X16} does not include node {nodeUid:X16}, which references it.");
        }
    }
}
