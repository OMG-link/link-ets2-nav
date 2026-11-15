using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

public abstract record MapQueryIssue(string Reason);
public enum MapQueryFailureKind { UnresolvedReference, Ambiguous, InvalidData, DefinitionUnavailable }
public sealed record MapQueryFailure(MapQueryFailureKind Kind,string Detail):MapQueryIssue(Detail);
/// <summary>Exact owning sectors supported by native reference evidence; never a guessed search radius.</summary>
public sealed record MissingMapSectors:MapQueryIssue
{
    public IReadOnlyList<SectorCoordinate> Sectors {get;}
    public MissingMapSectors(IEnumerable<SectorCoordinate> sectors,string reason):base(reason)
    {
        var values=sectors.Distinct().OrderBy(s=>s.X).ThenBy(s=>s.Z).ToArray();
        if(values.Length==0)throw new ArgumentException("A map dependency needs at least one exact sector.",nameof(sectors));
        Sectors=Array.AsReadOnly(values);
    }
}
/// <summary>Queries can expose a missing-sector retry without conflating it with an unresolved or invalid reference.</summary>
public readonly record struct MapQueryResult<T>(T? Value,MapQueryIssue? Issue)
{
    public bool IsSuccess=>Issue is null;
    public static MapQueryResult<T> Ready(T value)=>new(value,null);
    public static MapQueryResult<T> Unavailable(MapQueryIssue issue)=>new(default,issue);
}
