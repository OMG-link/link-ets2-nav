using System.Collections.ObjectModel;
using System.Numerics;
using TruckLib.ScsMap;
using NativeMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

public sealed record GameMapHeader(float NormalScale,float CityScale,Vector3 StartPosition,Quaternion StartRotation,bool EuropeMapUiCorrections,ulong EditorMapId);
public sealed record GameSectorData(SectorCoordinate Coordinate,Sector? Metadata,IReadOnlyList<MapItem> Items);

/// <summary>Unified borrowed native objects; provider ownership is kept inside this projection.</summary>
public sealed class GameMapData
{
    private readonly NativeMap? basis;
    private readonly bool retainSupplementHistory;
    private readonly HostMapView? view;
    private readonly NativeNodeConnections connections = new();
    private readonly UnresolvedItemReferenceCounts hostReferenceCounts = UnresolvedItemReferenceCounts.Empty;
    private readonly Dictionary<ulong, int> extraReferenceCounts = [];
    private IReadOnlyDictionary<SectorCoordinate,Sector> extraSectors=new Dictionary<SectorCoordinate,Sector>();
    private IReadOnlyDictionary<ulong,MapItem> extraItems=new Dictionary<ulong,MapItem>();
    private IReadOnlyDictionary<ulong,INode> extraNodes=new Dictionary<ulong,INode>();
    private readonly Dictionary<Type,IReadOnlyList<MapItem>> baseTypes=[];
    private readonly Dictionary<Type,object> typed=[];
    private readonly object gate=new();
    internal NativeMap? NativeBase=>basis;
    public GameMapHeader? Header {get;private set;}
    public IReadOnlyDictionary<ulong,MapItem> Items {get;}
    public IReadOnlyDictionary<ulong,INode> Nodes {get;}
    public IReadOnlyDictionary<SectorCoordinate,Sector> Sectors {get;}

    internal GameMapData(NativeMap? map,bool retainSupplementHistory=true)
    {
        basis=map;
        this.retainSupplementHistory=retainSupplementHistory;
        if(map is not null)
        {
            var unresolvedItems = new List<ulong>();
            connections.Validate(map.Nodes.Values, map.MapItems.Values,
                uid => map.Nodes.TryGetValue(uid, out var node) ? node : null,
                uid => map.MapItems.GetValueOrDefault(uid), unresolvedItems.Add);
            hostReferenceCounts = UnresolvedItemReferenceCounts.Build(unresolvedItems);
            view=new(map);
            foreach(var group in view.AllItemsByType)baseTypes[group.Key]=group.Value;
            Header=new(map.NormalScale,map.CityScale,map.StartPosition,map.StartRotation,map.EuropeMapUiCorrections,map.EditorMapId);
        }
        Items=new OverlayDictionary<ulong,MapItem>(map?.MapItems??[],()=>extraItems);
        Nodes=new OverlayDictionary<ulong,INode>(map is null?new Dictionary<ulong,INode>():new ReadOnlyDictionary<ulong,INode>(map.Nodes),()=>extraNodes);
        Sectors=new OverlayDictionary<SectorCoordinate,Sector>(map?.Sectors??[],()=>extraSectors);
    }
    public T? GetItem<T>(ulong uid) where T:MapItem=>Items.GetValueOrDefault(uid) as T;
    public MapItem? GetItem(ulong uid)=>Items.GetValueOrDefault(uid);
    public INode? GetNode(ulong uid)=>Nodes.GetValueOrDefault(uid);

    /// <summary>Gets the opposite side of a validated native connection. Success with a null value means an open end.</summary>
    public MapQueryResult<MapItem?> OtherConnectedItem(Node node, MapItem component)
        => NativeNodeConnections.Other(node, component, GetItem);

    public IReadOnlyList<T> GetItems<T>() where T:MapItem
    {
        lock(gate)
        {
            if(typed.TryGetValue(typeof(T),out var cached))return (IReadOnlyList<T>)cached;
            var values=baseTypes.Where(p=>typeof(T).IsAssignableFrom(p.Key)).SelectMany(p=>p.Value).Cast<T>()
                .Concat(extraItems.Values.OfType<T>()).ToArray();
            return (IReadOnlyList<T>)(typed[typeof(T)]=Array.AsReadOnly(values));
        }
    }
    public IReadOnlyList<MapItem> ConnectedItems(ulong nodeUid)
    {
        var node=GetNode(nodeUid) as Node;
        return node is null?[]:new[]{node.BackwardItem,node.ForwardItem}.Where(i=>i is not null)
            .Select(i=>GetItem(i.Uid)).OfType<MapItem>().DistinctBy(i=>i.Uid).ToArray();
    }
    public GameSectorData GetSector(SectorCoordinate coordinate)
    {
        lock(gate)
        {
            var items=(view?.ItemsIn(coordinate)??[]).Concat(extraItems.Values.Where(i=>HostMapView.NodesOf(i)
                .Any(n=>n is Node&&NativeMap.GetSectorOfCoordinate(n.Position)==coordinate))).DistinctBy(i=>i.Uid).ToArray();
            return new(coordinate,Sectors.GetValueOrDefault(coordinate),Array.AsReadOnly(items));
        }
    }
    internal void MergeInto(NativeMap map,IEnumerable<SectorCoordinate> sectors)=>view?.MergeInto(map,sectors);
    internal void Publish(NativeMap map)
    {
        lock(gate)
        {
            foreach (var (uid, incoming) in map.Nodes)
                if (incoming is Node node && GetNode(uid) is Node previous && !ReferenceEquals(previous, node))
                    NativeNodeConnections.ValidateRepeatedNode(previous, node);

            foreach (var item in map.MapItems.Values)
            {
                if (GetItem(item.Uid) is { } previous)
                {
                    if (!ReferenceEquals(previous, item))
                        NativeNodeConnections.ValidateRepeatedItem(previous, item);
                }
                else if (item is Road or Prefab)
                    NativeNodeConnections.ValidatePriorReferences(item,
                        hostReferenceCounts.Count(item.Uid) + extraReferenceCounts.GetValueOrDefault(item.Uid),
                        GetNode, (basis?.Nodes.Values ?? []).Concat(extraNodes.Values));
            }

            connections.Validate(map.Nodes.Values,map.MapItems.Values,NodeAt,ItemAt,retainMissingHistory:retainSupplementHistory);
            Header??=new(map.NormalScale,map.CityScale,map.StartPosition,map.StartRotation,map.EuropeMapUiCorrections,map.EditorMapId);
            // Standalone navigation can recover native objects by UID from its sector index.
            // Its local data is needed only in the current window.
            var items=retainSupplementHistory?extraItems.ToDictionary():new Dictionary<ulong,MapItem>();
            if(!retainSupplementHistory)extraReferenceCounts.Clear();
            foreach (var (uid, item) in map.MapItems)
            {
                if (basis?.MapItems.ContainsKey(uid) != true)
                    items[uid] = item;
                extraReferenceCounts.Remove(uid);
            }
            var nodes=retainSupplementHistory?extraNodes.ToDictionary():new Dictionary<ulong,INode>();
            foreach (var (uid, node) in map.Nodes)
            {
                if (basis?.Nodes.ContainsKey(uid) == true)
                    continue;
                if (!nodes.ContainsKey(uid))
                    RecordExtraReferences(node,items);
                nodes[uid] = node;
            }
            var sectors=retainSupplementHistory?extraSectors.ToDictionary():new Dictionary<SectorCoordinate,Sector>();
            foreach(var p in map.Sectors)if(basis?.Sectors.ContainsKey(p.Key)!=true)sectors[p.Key]=p.Value;
            extraItems=new ReadOnlyDictionary<ulong,MapItem>(items);
            extraNodes=new ReadOnlyDictionary<ulong,INode>(nodes);
            extraSectors=new ReadOnlyDictionary<SectorCoordinate,Sector>(sectors);
            typed.Clear();
        }

        INode? NodeAt(ulong uid)
        {
            if (basis?.Nodes.TryGetValue(uid, out var node) == true || map.Nodes.TryGetValue(uid, out node))
                return node;
            return extraNodes.GetValueOrDefault(uid);
        }

        MapItem? ItemAt(ulong uid)
            => basis?.MapItems.GetValueOrDefault(uid) ?? map.MapItems.GetValueOrDefault(uid) ?? extraItems.GetValueOrDefault(uid);

        void RecordExtraReferences(INode raw,Dictionary<ulong,MapItem> items)
        {
            if (raw is not Node node)
                return;
            var (backward, forward) = NativeNodeConnections.DistinctReferences(node);
            if (backward is ulong backwardUid)
                RecordReference(backwardUid,items);
            if (forward is ulong forwardUid)
                RecordReference(forwardUid,items);
        }

        void RecordReference(ulong uid,Dictionary<ulong,MapItem> items)
        {
            if (items.ContainsKey(uid)||basis?.MapItems.ContainsKey(uid)==true)
                return;
            extraReferenceCounts[uid] = extraReferenceCounts.GetValueOrDefault(uid) + 1;
        }
    }
}

internal sealed class OverlayDictionary<TKey,TValue>(IReadOnlyDictionary<TKey,TValue> basis,Func<IReadOnlyDictionary<TKey,TValue>> extras):IReadOnlyDictionary<TKey,TValue> where TKey:notnull
{
    public TValue this[TKey key]=>TryGetValue(key,out var value)?value:throw new KeyNotFoundException();
    public IEnumerable<TKey> Keys=>this.Select(p=>p.Key);
    public IEnumerable<TValue> Values=>this.Select(p=>p.Value);
    public int Count=>basis.Count+extras().Keys.Count(k=>!basis.ContainsKey(k));
    public bool ContainsKey(TKey key)=>basis.ContainsKey(key)||extras().ContainsKey(key);
    public bool TryGetValue(TKey key,out TValue value)
    {if(basis.TryGetValue(key,out value!))return true;return extras().TryGetValue(key,out value!);}
    public IEnumerator<KeyValuePair<TKey,TValue>> GetEnumerator()
    {foreach(var p in basis)yield return p;foreach(var p in extras())if(!basis.ContainsKey(p.Key))yield return p;}
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
}
