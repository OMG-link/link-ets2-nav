using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using LinkEts2Nav.Runtime;
using TruckLib;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using TruckLib.Sii;
using PpdSignal=TruckLib.Models.Ppd.Semaphore;

namespace LinkEts2Nav.Map;

public sealed record TrafficSignalTiming(SignalPhase NextAfterRed,string Source);

/// <summary>Static transition evidence only. Current phase and remaining time always come from the SDK.</summary>
public sealed class TrafficSignalDefinitions
{
    private readonly Dictionary<string,Unit> profiles=new(StringComparer.Ordinal);
    public TrafficSignalDefinitions(GameDataManager data)
    {
        foreach(string path in data.GetFiles("/def/world/").Where(p=>Path.GetFileName(p).StartsWith("semaphore_profile",StringComparison.Ordinal)
            &&p.EndsWith(".sii",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            data.TryDefinitions(path,document=>
            {foreach(var unit in document.OfClass("tr_semaphore_profile"))profiles[unit.Name]=unit;});
    }
    public TrafficSignalTiming? Resolve(Prefab prefab,PpdSignal signal,string? country,out string reason)
    {
        reason="灯组类型或相位配置尚未确认";
        var type=signal.Type;var intervals=signal.Intervals;string source="PPD";
        if(type==SemaphoreType.UseProfile)
        {
            string name=prefab.SemaphoreProfile.ToString();if(string.IsNullOrEmpty(name))name=signal.Profile.ToString();
            if(string.IsNullOrEmpty(name))return null;
            if(!name.StartsWith("tr_sem_prof.",StringComparison.Ordinal))name="tr_sem_prof."+name;
            if(country is not null&&profiles.ContainsKey(name+"."+country))name+="."+country;
            else if(country is null&&profiles.Keys.Any(k=>k.StartsWith(name+".",StringComparison.Ordinal)))
            {reason="灯组国家覆盖尚未确认";return null;}
            object? At(string key)=>Attribute(name,key,signal.SemaphoreId,[]);
            string? native=Text(At("type"));
            type=native switch
            {
                "traffic_light"=>SemaphoreType.TrafficLight,
                "traffic_light_minor"=>SemaphoreType.TrafficLightMinor,
                "traffic_light_major"=>SemaphoreType.TrafficLightMajor,
                _=>SemaphoreType.UseProfile
            };
            if(!Vector(At("interval"),out intervals))return null;
            source=name;
        }
        if(type is not (SemaphoreType.TrafficLight or SemaphoreType.TrafficLightMinor or SemaphoreType.TrafficLightMajor))
        {reason="非固定普通信号灯（动态控制、视觉副本或未知类型）";return null;}
        if(!float.IsFinite(intervals.X)||!float.IsFinite(intervals.Y)||!float.IsFinite(intervals.Z)||!float.IsFinite(intervals.W)
            ||intervals.X<=0||intervals.Y<0||intervals.Z<=0||intervals.W<0)return null;
        reason="已确认普通信号灯红灯后的下一相位";
        return new(intervals.W>0?SignalPhase.OrangeToGreen:SignalPhase.Green,source);
    }
    private object? Attribute(string name,string key,uint index,HashSet<string> seen)
    {
        if(!seen.Add(name)||!profiles.TryGetValue(name,out var unit))return null;
        if(unit.Attributes.TryGetValue(key,out var value)&&value is IEnumerable list&&value is not string)
        {
            var items=list.Cast<object>().ToArray();
            if(items.Length>0)return items[(int)Math.Min(index,(uint)(items.Length-1))];
        }
        return Text(unit.Attributes.GetValueOrDefault("inherited")) is {} parent?Attribute(parent,key,index,seen):null;
    }
    private static object? Value(object? value)=>value is null?null:value.GetType().GetProperty("Value")?.GetValue(value)??value;
    private static string? Text(object? value)
    {
        value=Value(value);string? text=value is Token token?token.ToString():Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim('"');
        return string.IsNullOrWhiteSpace(text)?null:text;
    }
    private static bool Vector(object? value,out Vector4 result)
    {
        value=Value(value);result=default;
        if(value is Vector4 vector){result=vector;return true;}
        // SII float quadruples use SCS quaternion order (w,x,y,z), even for non-rotation data.
        if(value is Quaternion quaternion){result=new(quaternion.W,quaternion.X,quaternion.Y,quaternion.Z);return true;}
        if(value is not ITuple {Length:4} tuple)return false;
        var numbers=new float[4];
        for(int i=0;i<4;i++)if(!float.TryParse(Text(tuple[i]),NumberStyles.Float,CultureInfo.InvariantCulture,out numbers[i]))return false;
        result=new(numbers[0],numbers[1],numbers[2],numbers[3]);return true;
    }
}

public sealed record TrafficSignalLamp(Vector3 Position,Quaternion Rotation);
public sealed record TrafficSignalControl(ulong PrefabUid,int CurveIndex,int SignalId,Vector3 StopPoint,Vector3 Forward,
    IReadOnlyList<TrafficSignalLamp> Lamps,TrafficSignalTiming? Timing,string Reason)
{
    public string Id=>$"{PrefabUid:X16}:{SignalId}";
}
public sealed class TrafficSignalMap(IEnumerable<TrafficSignalControl> controls)
{
    private readonly IReadOnlyDictionary<(ulong,int),TrafficSignalControl> curves=controls.ToDictionary(c=>(c.PrefabUid,c.CurveIndex));
    public static TrafficSignalMap Empty {get;}=new([]);
    public TrafficSignalControl? Find(ulong prefab,int curve)=>curves.GetValueOrDefault((prefab,curve));
    public int Count=>curves.Count;
    public static IReadOnlyList<TrafficSignalControl> Build(Prefab prefab,PrefabDescriptor descriptor,
        TrafficSignalDefinitions definitions,SpeedLimitData limits)
    {
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return [];
        var countries=limits.Roads.Values.Where(r=>prefab.Nodes.Any(n=>n.Uid==r.Geometry.StartNodeUid||n.Uid==r.Geometry.EndNodeUid))
            .Where(r=>!r.CountryUncertain).Select(r=>r.Country).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        string? country=countries.Length==1?countries[0]:countries.Length>1?null:limits.Country;
        var groups=new Dictionary<int,(TrafficSignalLamp[] Lamps,TrafficSignalTiming? Timing,string Reason)>();
        var z=placement.ToWorldDirection(Vector3.UnitZ);var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.Atan2(z.X,z.Z));
        var result=new List<TrafficSignalControl>();
        for(int index=0;index<descriptor.NavCurves.Count;index++)
        {
            var curve=descriptor.NavCurves[index];if(curve.SemaphoreId<0||curve.Length<=0)continue;
            if(!groups.TryGetValue(curve.SemaphoreId,out var group))
            {
                var semaphores=descriptor.Semaphores.Where(s=>s.SemaphoreId==(uint)curve.SemaphoreId).ToArray();
                var resolved=new List<TrafficSignalTiming>();string reason="曲线引用的灯组不存在";bool uncertain=false;
                foreach(var signal in semaphores.Where(s=>s.Type!=SemaphoreType.ModelOnly))
                {
                    var timing=definitions.Resolve(prefab,signal,country,out reason);
                    if(timing is not null)resolved.Add(timing);else uncertain=true;
                }
                var distinct=resolved.Distinct().ToArray();
                TrafficSignalTiming? confirmed=!uncertain&&distinct.Length==1?distinct[0]:null;
                if(prefab.CustomSemaphores){confirmed=null;reason="自定义灯组移除规则尚未确认";}
                group=(semaphores.Select(s=>new TrafficSignalLamp(placement.ToWorld(s.Position),Quaternion.Normalize(rotation*s.Rotation))).ToArray(),
                    confirmed,confirmed is null?reason:"已确认受控曲线灯组");
                groups[curve.SemaphoreId]=group;
            }
            var forward=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curve.StartRotation));forward.Y=0;
            if(forward.LengthSquared()<.001f)continue;
            result.Add(new(prefab.Uid,index,curve.SemaphoreId,placement.ToWorld(curve.StartPosition),Vector3.Normalize(forward),
                group.Lamps,group.Timing,group.Reason));
        }
        return result;
    }
}
