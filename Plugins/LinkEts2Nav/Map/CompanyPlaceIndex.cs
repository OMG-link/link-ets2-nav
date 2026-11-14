using System.Globalization;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

public sealed record CompanyPlace(ulong Uid,ulong PrefabUid,string CompanyToken,string CityToken,string? DisplayName);
/// <summary>Explicit company ownership only: no nearest-place or task-target guessing.</summary>
public sealed class CompanyPlaceIndex
{
    public static CompanyPlaceIndex Empty {get;}=new([],[]);
    public IReadOnlyList<CompanyPlace> Places {get;}
    private readonly Dictionary<ulong,ulong[]> prefabsByTerminal;
    public CompanyPlaceIndex(IReadOnlyList<CompanyPlace> places,IEnumerable<Prefab> prefabs)
    {
        Places=places;
        prefabsByTerminal=prefabs.SelectMany(p=>p.Nodes.Select(n=>(n.Uid,p.Uid)))
            .GroupBy(p=>p.Item1).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Item2).Distinct().ToArray());
    }
    public CompanyPlace? AtTerminal(ulong nodeUid)
    {
        if(!prefabsByTerminal.TryGetValue(nodeUid,out var prefabs))return null;
        var candidates=Places.Where(c=>prefabs.Contains(c.PrefabUid)).ToArray();
        return candidates.Length==1&&candidates[0].DisplayName is not null?candidates[0]:null;
    }
}

/// <summary>Company definitions are loaded once on the scanner worker, only when a nearby company exists.</summary>
public sealed class CompanyPlaceReader(IFileSystem fs)
{
    private Dictionary<string,string?>? names;
    public List<string> Diagnostics {get;}=[];
    public IReadOnlyDictionary<string,string?> ReadNames()=>names??=ReadDefinitions();
    public CompanyPlaceIndex Build(TruckLib.ScsMap.Map map)
    {
        var companies=LoadedMapItems.Enumerate(map.MapItems.Values).OfType<Company>().ToArray();
        if(companies.Length==0)return CompanyPlaceIndex.Empty;
        names??=ReadDefinitions();
        return new(companies.Where(c=>c.Prefab is not null).Select(c=>new CompanyPlace(c.Uid,c.Prefab.Uid,
            c.CompanyName.ToString(),c.CityName.ToString(),names.GetValueOrDefault(c.CompanyName.ToString()))).ToArray(),map.MapItems.Values.OfType<Prefab>());
    }
    private Dictionary<string,string?> ReadDefinitions()
    {
        var result=new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach(var path in fs.GetFiles("/def/").Where(p=>fs.GetParent(p).TrimEnd('/')=="/def"&&Path.GetFileName(p).StartsWith("company",StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order())
        try
        {
            foreach(var unit in SiiFile.Open(path,fs).Units.Where(u=>u.Class=="company_permanent"))
            {
                if(!unit.Attributes.TryGetValue("name",out var v)||v is null)continue;
                object value=v;string name=(Convert.ToString(value.GetType().GetProperty("Value")?.GetValue(value)??value,CultureInfo.InvariantCulture)??"").Trim('"').Trim().Normalize();
                result[unit.Name.Split('.').Last()]=name.Length is >0 and <=128&&!name.Contains("@@",StringComparison.Ordinal)&&!name.Any(char.IsControl)?name:null;
                if(result[unit.Name.Split('.').Last()] is null)Diagnostics.Add("货场显示名未解析："+unit.Name+"，定义="+path+"，原值="+name);
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException or FormatException or ArgumentException)
        {Diagnostics.Add("货场名称定义加载失败："+path+"，"+e.GetType().Name+"："+e.Message);}
        return result;
    }
}
