using System.Numerics;
namespace LinkEts2Nav.Map;

public abstract record MapQueryIssue(string Reason)
{
    /// <summary>The actual local query seed, not the requested sector's center.</summary>
    public Vector3? QueryAnchor {get;init;}
}
public enum MapQueryFailureKind { UnresolvedReference, Ambiguous, InvalidData, DefinitionUnavailable }
public sealed record MapQueryFailure(MapQueryFailureKind Kind,string Detail):MapQueryIssue(Detail);
public enum MapObjectKind { Node, Item }
public readonly record struct MapObjectKey(MapObjectKind Kind,ulong Uid);
/// <summary>Exact missing lookups in a complete window. Keys never imply owning sectors or request disk reads.</summary>
public sealed record MissingMapObjects:MapQueryIssue
{
    public IReadOnlyList<MapObjectKey> Keys {get;}
    public MissingMapObjects(IEnumerable<MapObjectKey> keys,string reason):base(reason)
    {
        var values=keys.Distinct().OrderBy(k=>k.Kind).ThenBy(k=>k.Uid).ToArray();
        if(values.Length==0)throw new ArgumentException("A map dependency needs at least one missing lookup key.",nameof(keys));
        Keys=Array.AsReadOnly(values);
    }
}
/// <summary>A missing-key result may retain its known local value while awaiting the rest of the operation.</summary>
public readonly record struct MapQueryResult<T>(T? Value,MapQueryIssue? Issue)
{
    public bool IsSuccess=>Issue is null;
    public static MapQueryResult<T> Ready(T value)=>new(value,null);
    public static MapQueryResult<T> Unavailable(MapQueryIssue issue)=>new(default,issue);
}
