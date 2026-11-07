using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using LinkEts2Nav.Core;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

public sealed class SpeedLimitDefinitionReader
{
    private readonly Dictionary<string,LimitAction> signs=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> cityCountries=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string?> roadClasses=new(StringComparer.Ordinal);
    private readonly Dictionary<string,TruckSpeedLimit> limits=new(StringComparer.Ordinal);
    private readonly Dictionary<string,Unit> rules=new(StringComparer.Ordinal);
    private string? error;

    public SpeedLimitDefinitionReader(IFileSystem original,IReadOnlyList<string>? mods=null)
    {
        try
        {
            var fs=SpeedLimitFileSystem.Create(original,mods??[]);
            IEnumerable<Unit> Files(string directory,string prefix) => fs.GetFiles(directory)
                .Where(p=>fs.GetParent(p).TrimEnd('/')==directory.TrimEnd('/')
                    && Path.GetFileName(p).StartsWith(prefix,StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).SelectMany(p=>SiiFile.Open(p,fs).Units);
            foreach(var u in Files("/def/world/","traffic_rules"))rules[u.Name]=u;
            var lanes=Files("/def/world/","traffic_lane").Where(u=>u.Class=="traffic_lane_data")
                .GroupBy(u=>u.Name).ToDictionary(g=>g.Key,g=>Text(g.Last(),"speed_class"));
            foreach(var u in Files("/def/world/","road_look").Where(u=>u.Attributes.ContainsKey("lanes_right")||u.Attributes.ContainsKey("lanes_left")))
            {
                var classes=Values(u,"lanes_left").Concat(Values(u,"lanes_right")).Select(Link)
                    .Select(p=>lanes.GetValueOrDefault(p)).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToArray();
                roadClasses[u.Name.Split('.').Last()]=classes.Length==1?classes[0]:null;
            }
            foreach(var u in Files("/def/world/","sign").Where(u=>u.Class=="sign_model"))
                signs[u.Name.Split('.').Last()]=Action(Link(Value(u,"traffic_rule")),[]);
            foreach(var u in Files("/def/","city").Where(u=>u.Class=="city_data"))
                cityCountries[u.Name.Split('.').Last()]=Link(Value(u,"country")).Split('.').Last();
            var countries=Files("/def/","country").Where(u=>u.Class=="country_data").Select(u=>u.Name.Split('.').Last())
                .Concat(cityCountries.Values).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToArray();
            foreach(string country in countries)
            {
                string directory="/def/country/"+country;
                if(!fs.DirectoryExists(directory))continue;
                foreach(var u in fs.GetFiles(directory).Where(p=>Path.GetFileName(p).StartsWith("speed_limits",StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal))
                    .OrderBy(p=>Path.GetFileName(p)=="speed_limits.sii"?0:1).ThenBy(p=>p,StringComparer.Ordinal)
                    .SelectMany(p=>SiiFile.Open(p,fs).Units).Where(u=>Text(u,"vehicle_speed_class")=="truck"))
                {
                    var classes=Values(u,"lane_speed_class").Select(Link).ToArray();
                    var rural=Numbers(u,"limit");var urban=Numbers(u,"urban_limit");var maximum=Numbers(u,"max_limit");
                    for(int i=0;i<classes.Length;i++)if(i<rural.Length&&i<urban.Length)
                        limits[country+"/"+classes[i]]=new(rural[i],urban[i],i<maximum.Length?maximum[i]:0);
                }
            }
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        { error="限速定义加载失败："+ex.GetType().Name+": "+ex.Message; }
    }

    public SpeedLimitData Build(TruckLib.ScsMap.Map map)
    {
        if(error is not null)return SpeedLimitData.Empty with{Error=error};
        var roads=new Dictionary<ulong,LimitRoad>();
        foreach(var road in map.MapItems.Values.OfType<Road>().Where(r=>r.Node is Node&&r.ForwardNode is Node))
        {
            int count=Math.Clamp((int)Math.Ceiling(road.Length/5),16,256);
            var points=Enumerable.Range(0,count+1).Select(i=>road.InterpolateCurve((float)i/count).Position).ToArray();
            string? cls=roadClasses.GetValueOrDefault(road.RoadType.ToString());
            if(!string.IsNullOrEmpty(road.Left.TrafficRule.ToString())||!string.IsNullOrEmpty(road.Right.TrafficRule.ToString()))cls=null;
            roads[road.Uid]=new(new(road.Uid.ToString("X16"),road.Node.Uid,road.ForwardNode.Uid,points),cls);
        }
        var placedSigns=new List<LimitSign>();
        foreach(var sign in map.MapItems.Values.OfType<Sign>().Where(s=>s.Node is Node))
            if(signs.TryGetValue(sign.Model.ToString(),out var action)&&action.Relevant)
                placedSigns.Add(new(sign.Uid.ToString("X16"),sign.Node.Position,Vector3.Transform(-Vector3.UnitZ,sign.Node.Rotation),
                    sign.FollowRoadDir,action,!string.IsNullOrEmpty(sign.SignTemplate)||sign.SignOverrides?.Count>0));
        placedSigns=placedSigns.Select(sign =>
        {
            var owners=roads.Values.Select(r=>(Road:r,Projection:SpeedLimitResolver.Nearest(sign.Position,r.Geometry)))
                .Where(p=>p.Projection is {Error:<=18,Vertical:<=3}).OrderBy(p=>p.Projection!.Value.Error).Take(2).ToArray();
            return sign with { RoadId=owners.Length==0?null:owners[0].Road.Geometry.Id,
                OwnershipAmbiguous=owners.Length==0||owners.Length>1&&owners[1].Projection!.Value.Error-owners[0].Projection!.Value.Error<2 };
        }).ToList();
        var regions=new List<LimitRegion>();
        var countries=new HashSet<string>(StringComparer.Ordinal);
        foreach(var city in map.MapItems.Values.OfType<CityArea>().Where(c=>c.Node is Node))
        {
            if(city.Node.Rotation!=Quaternion.Identity)continue;
            var p=city.Node.Position;float x=city.Width/2,z=city.Height/2;
            var country=cityCountries.GetValueOrDefault(city.Name.ToString());
            if(!string.IsNullOrEmpty(country))countries.Add(country);
            regions.Add(new(city.Uid.ToString("X16"),[p+new Vector3(-x,0,-z),p+new Vector3(x,0,-z),
                p+new Vector3(x,0,z),p+new Vector3(-x,0,z)],0,true){Country=country});
        }
        foreach(var area in map.MapItems.Values.OfType<TrafficArea>())
        {
            var action=Action("traffic_rule."+area.Rule,[]);
            if(action.Urban==true||action.SetKph.HasValue)
                regions.Add(new(area.Uid.ToString("X16"),area.Nodes.Select(n=>n.Position).ToArray(),area.Range,
                    action.Urban==true,action.SetKph));
        }
        return new(roads,placedSigns,regions,limits,countries.Count==1?countries.Single():null);
    }
    private LimitAction Action(string name,HashSet<string> seen)
    {
        if(string.IsNullOrEmpty(name))return new();
        if(!seen.Add(name))return new(Unsupported:true);
        if(name is "traffic_rule.urban_area" or "traffic_rule.urban_start")return new(Urban:true);
        if(name=="traffic_rule.urban_end")return new(Reset:true,Urban:false);
        if(name=="traffic_rule.crossroad_end")return new(Reset:true);
        if(!rules.TryGetValue(name,out var u))return new(Unsupported:name.Contains("limit",StringComparison.Ordinal)||name.Contains("urban",StringComparison.Ordinal));
        if(name.Contains("motorway",StringComparison.Ordinal)||name.Contains("highway",StringComparison.Ordinal))return new(Unsupported:true);
        int? speed=null;bool reset=false,unsupported=false;bool? urban=null;
        if(Text(u,"rule")=="speed_limit")
        {
            var vehicles=Values(u,"str_params").Select(Link).ToArray();
            if(vehicles.Length>0&&!vehicles.Any(v=>v is "truck" or "truck_all"))return new();
            var numbers=Numbers(u,"num_params");
            if(numbers.Length==0)unsupported=true;
            else if(numbers[0]==0)reset=true;
            else if(numbers[0] is >0 and <=300)speed=numbers[0];
            else unsupported=true;
        }
        foreach(var child in Values(u,"additional_rules").Select(Link))
        {
            var a=Action(child,new HashSet<string>(seen));
            speed=a.SetKph??speed;reset|=a.Reset;urban=a.Urban??urban;unsupported|=a.Unsupported;
        }
        return new(speed,reset,urban,unsupported);
    }
    private static object? Value(Unit u,string key)=>u.Attributes.TryGetValue(key,out var value)?(object)value:null;
    private static string Text(Unit u,string key)=>Link(Value(u,key));
    private static IEnumerable<object> Values(Unit u,string key)
    {var value=Value(u,key);return value is IEnumerable list&&value is not string?list.Cast<object>():value is null?[]:[value];}
    private static int[] Numbers(Unit u,string key)=>Values(u,key).Select(v=>(int)Math.Round(double.Parse(Link(v),CultureInfo.InvariantCulture))).ToArray();
    private static string Link(object? value)
    {
        if(value is null)return "";
        if(value is Token token)return token.ToString();
        var property=value.GetType().GetProperty("Value");
        if(property is not null)return Convert.ToString(property.GetValue(value),CultureInfo.InvariantCulture)??"";
        string s=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        var m=Regex.Match(s,@"Value = ([^ }]+)");
        return m.Success?m.Groups[1].Value:s.Trim('"');
    }
}
