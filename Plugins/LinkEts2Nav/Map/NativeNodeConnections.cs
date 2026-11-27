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
        Func<ulong, INode?> getNode, Func<ulong, MapItem?> getItem, Action<ulong>? onUnresolvedItem = null,
        bool retainMissingHistory = true)
    {
        var nextNodes = new HashSet<NodeReference>();
        foreach (var reference in pendingNodes)
            if(retainMissingHistory||getNode(reference.NodeUid) is Node)
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

        ValidateReferences(nodes, getItem, onUnresolvedItem);
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

    public static (ulong? Backward, ulong? Forward) DistinctReferences(Node node)
    {
        ulong? backward = node.BackwardItem?.Uid;
        ulong? forward = node.ForwardItem?.Uid;
        return (backward, forward == backward ? null : forward);
    }

    public static void ValidateRepeatedNode(Node previous, Node incoming)
    {
        if (previous.BackwardItem?.Uid == incoming.BackwardItem?.Uid
            && previous.ForwardItem?.Uid == incoming.ForwardItem?.Uid)
            return;

        throw new InvalidDataException($"Native node {incoming.Uid:X16} changed its connection declarations. "
            + $"Before: {previous.BackwardItem?.Uid:X16} / {previous.ForwardItem?.Uid:X16}. "
            + $"After: {incoming.BackwardItem?.Uid:X16} / {incoming.ForwardItem?.Uid:X16} (backward / forward).");
    }

    public static void ValidateRepeatedItem(MapItem previous, MapItem incoming)
    {
        if (previous is not (Road or Prefab) && incoming is not (Road or Prefab))
            return;

        bool same = (previous, incoming) switch
        {
            (Road oldRoad, Road road) => oldRoad.Node?.Uid == road.Node?.Uid
                && oldRoad.ForwardNode?.Uid == road.ForwardNode?.Uid,
            (Prefab oldPrefab, Prefab prefab) => oldPrefab.Origin == prefab.Origin
                && oldPrefab.Nodes.Select(node => node?.Uid).SequenceEqual(prefab.Nodes.Select(node => node?.Uid)),
            _ => false
        };
        if (!same)
            throw new InvalidDataException($"Native component {incoming.Uid:X16} changed its interface declarations. "
                + $"Before: {Describe(previous)}. After: {Describe(incoming)}.");

        static string Describe(MapItem item)
        {
            var endpoints = HostMapView.NodesOf(item).Select(node => node is null ? "null" : $"{node.Uid:X16}");
            string origin = item is Prefab prefab ? $", origin {prefab.Origin}" : "";
            return $"{item.GetType().Name} [{string.Join(", ", endpoints)}]{origin}";
        }
    }

    /// <summary>The new component must account for every earlier node that references its UID.</summary>
    public static void ValidatePriorReferences(MapItem item, int priorCount, Func<ulong, INode?> getPriorNode,
        IEnumerable<INode> priorNodes)
    {
        if (priorCount == 0)
            return;
        int reciprocalCount = HostMapView.NodesOf(item)
            .Where(endpoint => endpoint is not null).DistinctBy(endpoint => endpoint.Uid)
            .Count(endpoint => getPriorNode(endpoint.Uid) is Node node
                && (node.BackwardItem?.Uid == item.Uid || node.ForwardItem?.Uid == item.Uid));
        // Each reciprocal node is one of the previously counted declarations.
        // Find the offending node only after proving that an unmatched declaration exists.
        if (priorCount > reciprocalCount)
            ValidateReferences(priorNodes, uid => uid == item.Uid ? item : null);
    }

    private static void ValidateReferences(IEnumerable<INode> nodes, Func<ulong, MapItem?> getItem,
        Action<ulong>? onUnresolvedItem = null)
    {
        foreach (var raw in nodes)
        {
            if (raw is not Node node)
                continue;
            var (backward, forward) = DistinctReferences(node);
            if (backward is ulong backwardUid)
                CheckReference(node.Uid, backwardUid);
            if (forward is ulong forwardUid)
                CheckReference(node.Uid, forwardUid);
        }

        void CheckReference(ulong nodeUid, ulong itemUid)
        {
            var item = getItem(itemUid);
            if (item is null)
                onUnresolvedItem?.Invoke(itemUid);
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
