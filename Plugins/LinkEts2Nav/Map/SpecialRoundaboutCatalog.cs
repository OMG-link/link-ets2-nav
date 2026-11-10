using System.Globalization;
using System.Text.Json;
namespace LinkEts2Nav.Map;

public sealed record SpecialRoundaboutMember(string Key, string? Resource, string? InstanceUid,
    int? CurveCount = null, int? ControlNodeCount = null);
public sealed record SpecialRoundaboutArc(string Member, int Curve, IReadOnlyList<int>? ExpectedNext);
public sealed record SpecialRoundaboutDefinition(string Id, string Description,
    IReadOnlyList<SpecialRoundaboutMember> Members, IReadOnlyList<SpecialRoundaboutArc> ReferenceCircuit);
public sealed record SpecialRoundaboutPrefab(string Resource, int CurveCount, int ControlNodeCount);

/// <summary>Explicitly reviewed exceptions. A reference circuit orders ports; it never adds native driving edges.</summary>
public sealed class SpecialRoundaboutCatalog
{
    private sealed record CatalogFile(int Schema, IReadOnlyList<SpecialRoundaboutDefinition> Definitions);
    public IReadOnlyList<SpecialRoundaboutDefinition> Definitions { get; }
    public static SpecialRoundaboutCatalog Default { get; } = LoadEmbedded();
    private static string Normalize(string path) => path.Replace('\\','/').ToLowerInvariant();
    public SpecialRoundaboutCatalog(IEnumerable<SpecialRoundaboutDefinition> definitions)
    {
        Definitions = definitions.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Definitions)
        {
            if (string.IsNullOrWhiteSpace(d.Id) || !ids.Add(d.Id) || d.Members.Count==0
                || d.ReferenceCircuit.Count==0 || !d.Members.Any(m=>m.Resource is not null))
                throw new InvalidDataException("Invalid special roundabout definition.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in d.Members)
                if (string.IsNullOrWhiteSpace(m.Key) || !keys.Add(m.Key)
                    || (d.Members.Count>1 || m.Resource is null) && !Uid(m.InstanceUid,out _)
                    || m.InstanceUid is not null && !Uid(m.InstanceUid,out _)
                    || m.Resource is not null && (m.CurveCount is null or <=0 || m.ControlNodeCount is null or <2))
                    throw new InvalidDataException("Invalid special roundabout member: "+d.Id);
            if(d.ReferenceCircuit.Any(a=>!keys.Contains(a.Member) || a.Curve>=0 && a.ExpectedNext is null)
                || d.ReferenceCircuit.Select(a=>(a.Member,a.Curve)).Distinct().Count()!=d.ReferenceCircuit.Count
                || keys.Any(key=>!d.ReferenceCircuit.Any(a=>a.Member==key)))
                throw new InvalidDataException("Invalid reference circuit: "+d.Id);
        }
    }
    public static SpecialRoundaboutCatalog Parse(string json)
    {
        var file=JsonSerializer.Deserialize<CatalogFile>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ?? throw new InvalidDataException("Empty special roundabout catalog.");
        if(file.Schema!=1)throw new InvalidDataException("Unsupported special roundabout catalog schema.");
        return new(file.Definitions);
    }
    private static SpecialRoundaboutCatalog LoadEmbedded()
    {
        using var stream=typeof(SpecialRoundaboutCatalog).Assembly.GetManifestResourceStream("LinkEts2Nav.special-roundabouts.json")
            ?? throw new InvalidDataException("Missing special roundabout catalog.");
        using var reader=new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    private static bool Uid(string? text,out ulong uid)
        => ulong.TryParse(text,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uid)&&uid!=0;

    public IEnumerable<RoundaboutStructure> Build(IReadOnlyDictionary<RoundaboutCurveKey,RoundaboutCurve> graph,
        IReadOnlyDictionary<ulong,SpecialRoundaboutPrefab> prefabs, IReadOnlySet<ulong> classified,
        Action<string>? diagnostic=null, CancellationToken token=default)
    {
        foreach(var definition in Definitions)
        {
            token.ThrowIfCancellationRequested();
            var first=definition.Members[0];
            IEnumerable<ulong> candidates=Uid(first.InstanceUid,out var fixedUid) ? [fixedUid]
                : prefabs.Where(p=>Normalize(p.Value.Resource)==Normalize(first.Resource!)).Select(p=>p.Key);
            foreach(var anchor in candidates)
            {
                var members=new Dictionary<string,ulong>(StringComparer.Ordinal);bool valid=true;
                foreach(var member in definition.Members)
                {
                    ulong uid=Uid(member.InstanceUid,out var explicitUid)?explicitUid:anchor;
                    if(member.Resource is { } resource)
                    {
                        if(!prefabs.TryGetValue(uid,out var known)||Normalize(known.Resource)!=Normalize(resource))
                        {valid=false;break;}
                        if(known.CurveCount!=member.CurveCount||known.ControlNodeCount!=member.ControlNodeCount)
                        {diagnostic?.Invoke($"Special roundabout {definition.Id}: resource structure changed ({uid:X16}).");valid=false;break;}
                    }
                    else if(!graph.Keys.Any(k=>k.ItemUid==uid)){valid=false;break;}
                    members.Add(member.Key,uid);
                }
                if(!valid || members.Values.Distinct().Count()!=members.Count || members.Values.Any(classified.Contains))continue;
                var circuit=definition.ReferenceCircuit.Select(a=>new RoundaboutCurveKey(members[a.Member],a.Curve)).ToArray();
                if(circuit.Any(k=>!graph.ContainsKey(k)))continue;
                if(definition.ReferenceCircuit.Select((arc,i)=>(arc,i)).Any(p=>p.arc.ExpectedNext is { } expected
                    && !graph[circuit[p.i]].Next.Where(k=>k.ItemUid==circuit[p.i].ItemUid).Select(k=>k.CurveIndex)
                        .Order().SequenceEqual(expected.Order())))
                {diagnostic?.Invoke($"Special roundabout {definition.Id}: native curve connectivity changed.");continue;}
                var found=new RoundaboutBuilder(graph.Values).BuildDeclaredCircuit(circuit,token);
                if(found is not null)yield return found with{SpecialDefinitionId=definition.Id};
            }
        }
    }
}
