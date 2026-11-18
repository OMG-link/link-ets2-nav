using TruckLib.ScsMap;
using GameMap=TruckLib.ScsMap.Map;
namespace LinkEts2Nav.Map;

/// <summary>Immutable native road endpoint evidence. Validation occurs only when a road is queried.</summary>
public sealed class RoadCountryIndex
{
    private sealed record Endpoint(ulong? Uid,bool Loaded,ulong Backward,ulong Forward,byte BackwardCountry,byte ForwardCountry);
    private sealed record Evidence(Endpoint Start,Endpoint End);
    private readonly IReadOnlyDictionary<ulong,Evidence> roads;
    private readonly IReadOnlyDictionary<byte,string> names;
    private RoadCountryIndex(IReadOnlyDictionary<ulong,Evidence> roads,IReadOnlyDictionary<byte,string> names)
    {this.roads=roads;this.names=names;}
    public bool Contains(ulong roadUid)=>roads.ContainsKey(roadUid);

    /// <summary>Only each endpoint's actual road-item side supplies a country. Missing nodes are map dependencies, never country zero.</summary>
    public MapQueryResult<string> QueryCountry(ulong roadUid,Func<ulong,MapQueryIssue>? missingConnection=null)
    {
        if(!roads.TryGetValue(roadUid,out var road))
            return MapQueryResult<string>.Unavailable(new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,$"Road {roadUid:X16} has no retained native country evidence."));
        foreach(var endpoint in new[]{road.Start,road.End})
        {
            if(endpoint.Uid is null)throw new InvalidDataException($"Road {roadUid:X16}: serialized endpoint reference is null.");
            if(!endpoint.Loaded)
                return MapQueryResult<string>.Unavailable(missingConnection?.Invoke(endpoint.Uid.Value)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,$"Road {roadUid:X16}: native endpoint {endpoint.Uid:X16} is not loaded; its country has not been read."));
        }
        byte first=Country(road.Start),last=Country(road.End);
        if(first==0||last==0||first!=last)
            throw new InvalidDataException($"Road {roadUid:X16}: endpoint countries must be equal and nonzero; node {road.Start.Uid:X16} has {first}, node {road.End.Uid:X16} has {last}.");
        if(!names.TryGetValue(first,out string? country)||string.IsNullOrWhiteSpace(country))
            throw new InvalidDataException($"Road {roadUid:X16}: native country ID {first} has no loaded country definition.");
        return MapQueryResult<string>.Ready(country);

        byte Country(Endpoint endpoint)
        {
            bool backward=endpoint.Backward==roadUid,forward=endpoint.Forward==roadUid;
            if(!backward&&!forward||backward&&forward&&endpoint.BackwardCountry!=endpoint.ForwardCountry)
                throw new InvalidDataException($"Road {roadUid:X16}: node {endpoint.Uid:X16} does not identify one consistent road-item country side (backward {endpoint.Backward:X16}/{endpoint.BackwardCountry}, forward {endpoint.Forward:X16}/{endpoint.ForwardCountry}).");
            return backward?endpoint.BackwardCountry:endpoint.ForwardCountry;
        }
    }

    public static RoadCountryIndex Build(GameMap map,IReadOnlyDictionary<byte,string> countryNames,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(map);ArgumentNullException.ThrowIfNull(countryNames);
        token.ThrowIfCancellationRequested();
        Endpoint Snapshot(INode? input)=>input is Node node
            ?new(node.Uid,true,node.BackwardItem?.Uid??0,node.ForwardItem?.Uid??0,node.BackwardCountry,node.ForwardCountry)
            :new(input?.Uid,false,0,0,0,0);
        var roads=new Dictionary<ulong,Evidence>();
        foreach(var road in map.MapItems.Values.OfType<Road>())
        {
            token.ThrowIfCancellationRequested();
            roads.Add(road.Uid,new(Snapshot(road.Node),Snapshot(road.ForwardNode)));
        }
        return new(roads,new Dictionary<byte,string>(countryNames));
    }
}
