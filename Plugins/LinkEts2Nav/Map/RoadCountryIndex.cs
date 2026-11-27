using TruckLib.ScsMap;
using GameMap=TruckLib.ScsMap.Map;
namespace LinkEts2Nav.Map;

/// <summary>Loaded native country evidence does not identify a unique country; map data is not missing.</summary>
public sealed record UndeterminedRoadCountry(string Detail):MapQueryIssue(Detail);

/// <summary>Immutable native road endpoint evidence. Validation occurs only when a road is queried.</summary>
public sealed class RoadCountryIndex
{
    private readonly record struct Endpoint(ulong? Uid,bool Loaded,ulong Backward,ulong Forward,byte BackwardCountry,byte ForwardCountry);
    private readonly record struct Evidence(Endpoint Start,Endpoint End);
    private readonly Dictionary<ulong,string> confirmed;
    private readonly Dictionary<ulong,Evidence> unresolved;
    private readonly IReadOnlyDictionary<byte,string> names;
    private RoadCountryIndex(Dictionary<ulong,string> confirmed,Dictionary<ulong,Evidence> unresolved,IReadOnlyDictionary<byte,string> names)
    {this.confirmed=confirmed;this.unresolved=unresolved;this.names=names;}
    public bool Contains(ulong roadUid)=>confirmed.ContainsKey(roadUid)||unresolved.ContainsKey(roadUid);

    /// <summary>Only each endpoint's actual road-item side supplies a country. Missing nodes are map dependencies, never country zero.</summary>
    public MapQueryResult<string> QueryCountry(ulong roadUid,Func<ulong,MapQueryIssue>? missingConnection=null)
    {
        if(confirmed.TryGetValue(roadUid,out var known))return MapQueryResult<string>.Ready(known);
        if(!unresolved.TryGetValue(roadUid,out var road))
            return MapQueryResult<string>.Unavailable(new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,$"Road {roadUid:X16} has no retained native country evidence."));
        foreach(var endpoint in new[]{road.Start,road.End})
        {
            if(endpoint.Uid is null)throw new InvalidDataException($"Road {roadUid:X16}: serialized endpoint reference is null.");
            if(!endpoint.Loaded)
                return MapQueryResult<string>.Unavailable(missingConnection?.Invoke(endpoint.Uid.Value)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,$"Road {roadUid:X16}: native endpoint {endpoint.Uid:X16} is not loaded; its country has not been read."));
        }
        byte first=Country(road.Start),last=Country(road.End);
        foreach(byte id in new[]{first,last}.Where(id=>id!=0))
            if(!names.TryGetValue(id,out var name)||string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Road {roadUid:X16}: native country ID {id} has no loaded country definition.");
        if(first==0&&last==0||first!=0&&last!=0&&first!=last)
            return MapQueryResult<string>.Unavailable(new UndeterminedRoadCountry(
                $"Road {roadUid:X16}: loaded endpoint countries do not identify one country; node {road.Start.Uid:X16} has {first}, node {road.End.Uid:X16} has {last}."));
        string country=names[first!=0?first:last];
        return MapQueryResult<string>.Ready(country);

        byte Country(Endpoint endpoint)
        {
            if(TryCountrySide(roadUid,endpoint,out byte country))return country;
            throw new InvalidDataException($"Road {roadUid:X16}: node {endpoint.Uid:X16} does not identify one consistent road-item country side (backward {endpoint.Backward:X16}/{endpoint.BackwardCountry}, forward {endpoint.Forward:X16}/{endpoint.ForwardCountry}).");
        }
    }

    private static bool TryCountrySide(ulong roadUid,Endpoint endpoint,out byte country)
    {
        bool backward=endpoint.Backward==roadUid,forward=endpoint.Forward==roadUid;
        country=backward?endpoint.BackwardCountry:endpoint.ForwardCountry;
        return (backward||forward)&&(!backward||!forward||endpoint.BackwardCountry==endpoint.ForwardCountry);
    }

    public static RoadCountryIndex Build(GameMap map,IReadOnlyDictionary<byte,string> countryNames,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(map);ArgumentNullException.ThrowIfNull(countryNames);
        token.ThrowIfCancellationRequested();
        Endpoint Snapshot(INode? input)=>input is Node node
            ?new(node.Uid,true,node.BackwardItem?.Uid??0,node.ForwardItem?.Uid??0,node.BackwardCountry,node.ForwardCountry)
            :new(input?.Uid,false,0,0,0,0);
        var names=new Dictionary<byte,string>(countryNames);
        var confirmed=new Dictionary<ulong,string>();
        var unresolved=new Dictionary<ulong,Evidence>();
        foreach(var road in map.MapItems.Values.OfType<Road>())
        {
            token.ThrowIfCancellationRequested();
            var first=Snapshot(road.Node);
            var last=Snapshot(road.ForwardNode);
            if(first.Loaded&&last.Loaded&&TryCountrySide(road.Uid,first,out byte a)&&TryCountrySide(road.Uid,last,out byte b)
                &&(a!=0||b!=0)&&(a==0||b==0||a==b)&&names.TryGetValue(a!=0?a:b,out var name)&&!string.IsNullOrWhiteSpace(name))
                confirmed.Add(road.Uid,name);
            else
                unresolved.Add(road.Uid,new(first,last));
        }
        return new(confirmed,unresolved,names);
    }
}
