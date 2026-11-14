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
    private readonly HostMapView? view;
    private NativeMap? window;
    private IReadOnlyDictionary<ulong,MapItem> extraItems=new Dictionary<ulong,MapItem>();
    private IReadOnlyDictionary<ulong,INode> extraNodes=new Dictionary<ulong,INode>();
    private readonly Dictionary<Type,IReadOnlyList<MapItem>> baseTypes=[];
    private readonly Dictionary<Type,object> typed=[];
    private readonly object gate=new();
    internal NativeMap? NativeBase=>basis;
    public GameMapHeader? Header {get;}
    public IReadOnlyDictionary<ulong,MapItem> Items {get;}
    public IReadOnlyDictionary<ulong,INode> Nodes {get;}
    public IReadOnlyDictionary<SectorCoordinate,Sector> Sectors {get;}

    internal GameMapData(NativeMap? map)
    {
        basis=map;
        if(map is not null)
        {
            view=new(map);
            foreach(var group in view.AllItemsByType)baseTypes[group.Key]=group.Value;
            Header=new(map.NormalScale,map.CityScale,map.StartPosition,map.StartRotation,map.EuropeMapUiCorrections,map.EditorMapId);
        }
        Items=new OverlayDictionary<ulong,MapItem>(map?.MapItems??[],()=>extraItems);
        Nodes=new OverlayDictionary<ulong,INode>(map is null?new Dictionary<ulong,INode>():new ReadOnlyDictionary<ulong,INode>(map.Nodes),()=>extraNodes);
        Sectors=new OverlayDictionary<SectorCoordinate,Sector>(map?.Sectors??[],()=>window?.Sectors??[]);
    }
    public T? GetItem<T>(ulong uid) where T:MapItem=>Items.GetValueOrDefault(uid) as T;
    public MapItem? GetItem(ulong uid)=>Items.GetValueOrDefault(uid);
    public INode? GetNode(ulong uid)=>Nodes.GetValueOrDefault(uid);
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
            extraItems=new ReadOnlyDictionary<ulong,MapItem>(map.MapItems.Where(p=>basis?.MapItems.ContainsKey(p.Key)!=true).ToDictionary());
            extraNodes=new ReadOnlyDictionary<ulong,INode>(map.Nodes.Where(p=>basis?.Nodes.ContainsKey(p.Key)!=true).ToDictionary());
            window=map;typed.Clear();
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
