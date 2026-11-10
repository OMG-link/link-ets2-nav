using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.Map;

/// <summary>Simulates temporary limits only along the selected player-to-camera path.</summary>
public sealed class SpeedLimitResolver
{
    private readonly SpeedLimitData data;
    private readonly RoadPathSource paths;
    private readonly DirectedPathMatcher matcher=new();
    public SpeedLimitResolver(SpeedLimitData data,RoadPathSource paths)
    {
        this.paths=paths;
        this.data=data.SignProjections is not null||data.Error is not null?data:
            ProjectedSignBuilder.Build(data,paths.GetNavigationGraph().Where(e=>e.Id.StartsWith("prefab:")).ToArray());
    }
    public SpeedLimitResult ResolveToCamera(GameDataSnapshot game,MonitoredCamera camera)
    {
        if(game.Vehicle is not {} vehicle)return new(null,"车辆数据不可用");
        var position=new Vector3((float)vehicle.X,(float)vehicle.Y,(float)vehicle.Z);
        var route=paths.GetPath(position,vehicle.ForwardDirection,game.Route,game.RouteAvailability);
        return ResolveOnPath(position,vehicle.ForwardDirection,vehicle.CurrentSpeedLimitKph,camera,route);
    }
    public SpeedLimitResult ResolveOnPath(Vector3 position,Vector3 heading,double? currentSdkKph,
        MonitoredCamera camera,IReadOnlyList<DirectedRoad> route)
    {
        if(data.Error is not null)return new(null,data.Error);
        if(route.Count==0||route.Zip(route.Skip(1)).Any(p=>p.First.EndNodeUid!=p.Second.StartNodeUid))
            return new(null,"到探头的路径尚未连续");
        var start=matcher.ProjectDistance(position,heading,route);
        var goal=matcher.ProjectDistance(camera.MonitoringPosition,camera.TravelDirection,route);
        if(start is null||goal is null||goal<start-.01)return new(null,"当前位置或探头未匹配同一条前进路径");
        int? temporary=currentSdkKph is >0 and <=300?(int)Math.Round(currentSdkKph.Value):null;
        bool unknown=temporary is null;bool? urban=null,motorway=null;
        string reason=temporary.HasValue?"SDK 当前限速初始化":"当前限速种子不可用";
        double offset=0;DirectedRoad? targetRoad=null;string? lastSign=null;
        foreach(var road in route)
        {
            double length=road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            if(offset>goal.Value+.01)break;
            if(offset+length>=start.Value-.01)
            {
                if(!data.SignProjections!.Contains(road))return new(null,"路径区段的标牌投影尚未加载");
                // RoadPathSource and the loader may use different polyline sampling densities.
                // Reproject the cached point, rather than reusing another polyline's distance.
                var events=data.SignProjections.On(road).Select(sign=>(Sign:sign,
                    At:SpeedLimitGeometry.Nearest(sign.Position,road)?.Along))
                    .Where(p=>p.At.HasValue).OrderBy(p=>p.At).ThenBy(p=>p.Sign.SignId,StringComparer.Ordinal);
                foreach(var item in events)
                {
                    double at=offset+item.At!.Value;
                    if(at<=start.Value+.01||at>goal.Value+.01||lastSign==item.Sign.SignId)continue;
                    lastSign=item.Sign.SignId;var action=item.Sign.Action;
                    if(action.Unsupported){unknown=true;reason="路径标牌未确认："+lastSign;continue;}
                    // There is one active temporary value, not a stack. One reset clears it all.
                    if(action.Reset){temporary=null;unknown=false;}
                    if(action.SetKph.HasValue){temporary=action.SetKph;unknown=false;}
                    if(action.Urban.HasValue)urban=action.Urban;
                    if(action.Motorway.HasValue)motorway=action.Motorway;
                    reason=(action.Reset&&!action.SetKph.HasValue?"解除标牌 ":"路径标牌 ")+lastSign;
                }
            }
            targetRoad=road;offset+=length;
        }
        if(unknown)return new(null,reason);
        return Compose(camera.MonitoringPosition,targetRoad!,temporary,urban,motorway,reason);
    }
    // Country, road class, area and maximum-limit composition are unchanged.
    private SpeedLimitResult Compose(Vector3 position,DirectedRoad road,int? temporary,bool? urbanOverride,bool? motorway,string reason)
    {
        var metadata=data.SignProjections!.Context(road);
        if(metadata?.CountryUncertain==true)return new(null,"道路国家证据冲突或国界国家ID未知");
        var regions=data.RegionIndex!.At(position);
        var countries=regions.Where(r=>r.Country is not null).Select(r=>r.Country!).Distinct().ToArray();
        if(countries.Length>1 || countries.Length==1&&metadata?.Country is {} rc&&rc!=countries[0])
            return new(null,"道路国家证据冲突或国界国家ID未知");
        string? country=metadata?.Country??countries.SingleOrDefault()??data.Country;
        if(country is null)return new(null,"国家归属未知");
        if(regions.Any(r=>r.Unsupported))return new(null,"所在交通区域包含未支持的限速规则");
        string? speedClass=metadata?.ClassFor(road);
        if(motorway==true)speedClass="motorway";
        if(speedClass is null)return new(null,"监测道路车道类别或变体未支持");
        if(!data.Limits.TryGetValue(country+"/"+speedClass,out var rule))
            return new(null,"缺少货车限速规则："+country+"/"+speedClass);
        bool urban=urbanOverride??regions.Any(r=>r.Urban);
        int baseline=urban?rule.Urban:rule.Rural;
        int? value=temporary??(baseline>0?baseline:null);
        if(value is not >0)return new(null,"道路默认限速或解除后的限速未知");
        if(rule.Maximum>0)value=Math.Min(value.Value,rule.Maximum);
        var area=regions.Where(r=>r.SetKph is >0).Select(r=>r.SetKph!.Value).ToArray();
        if(area.Length>0)value=Math.Min(value.Value,area.Min());
        if(temporary is null)reason+="；"+(urban?"城区默认限速":"国家道路默认限速");
        if(area.Length>0)reason+="；交通区域限速";
        return new(value,"实验估算："+reason+"；"+country+"/"+speedClass);
    }
}
