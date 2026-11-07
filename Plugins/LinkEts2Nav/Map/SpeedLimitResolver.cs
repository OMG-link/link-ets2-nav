using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.Map;

/// <summary>Experimental local rule replay, using the camera's monitored road and direction.</summary>
public sealed class SpeedLimitResolver(SpeedLimitData data, RoadPathSource paths)
{
    private readonly DirectedPathMatcher matcher=new();
    private readonly Dictionary<(string Id,Vector3 Position,Vector3 Direction),SpeedLimitResult> cache=new();
    public SpeedLimitResult Resolve(MonitoredCamera camera)
    {
        var key=(camera.Id,camera.MonitoringPosition,camera.TravelDirection);
        if(cache.TryGetValue(key,out var cached))return cached;
        var result=ResolveCore(camera);
        cache[key]=result;
        return result;
    }

    private SpeedLimitResult ResolveCore(MonitoredCamera camera)
    {
        if(data.Error is not null)return new(null,data.Error);
        var backwards=paths.GetPath(camera.MonitoringPosition,-camera.TravelDirection,
            RouteSnapshot.Parse([],DateTimeOffset.MinValue),RouteAvailability.None);
        if(backwards.Count==0)return new(null,"监测点未唯一匹配道路");
        var forward=backwards.Reverse().Select(r=>new DirectedRoad(r.Id,r.EndNodeUid,r.StartNodeUid,r.Points.Reverse().ToArray())).ToArray();
        var cameraAt=matcher.ProjectDistance(camera.MonitoringPosition,camera.TravelDirection,forward);
        if(cameraAt is null)return new(null,"限速回溯路径不连续或方向不确定");
        var events=new List<(double At,int Order,LimitAction? Action,int? Base,string Reason)>();
        double offset=0;
        foreach(var road in forward)
        {
            LimitRoad? metadata=null;
            if(ulong.TryParse(road.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var uid))
                data.Roads.TryGetValue(uid,out metadata);
            if(metadata is null)
                events.Add((offset,0,new(Unsupported:true),null,"预制件内部限速传播尚未确认"));
            double along=0;
            for(int i=0;metadata is not null&&i<road.Points.Count;i++)
            {
                if(i>0)along+=Vector3.Distance(road.Points[i-1],road.Points[i]);
                if(offset+along>cameraAt.Value)break;
                var value=BaseAt(road.Points[i],metadata?.SpeedClass,metadata?.Country);
                events.Add((offset+along,0,null,value.Kph,value.Reason));
            }
            foreach(var sign in data.Signs)
            {
                var projection=Nearest(sign.Position,road);
                if(projection is null||projection.Value.Error>18||projection.Value.Vertical>3)continue;
                if(offset+projection.Value.Along>cameraAt.Value+.01)continue;
                if(sign.RoadId is not null&&sign.RoadId!=road.Id)continue;
                // Production associations are built by the scanner, outside the warning loop.
                // Synthetic/unassociated inputs can still resolve ownership here.
                var owners=(sign.RoadId is null ? data.Roads.Values : Array.Empty<LimitRoad>()).Select(r=>(Road:r,Projection:Nearest(sign.Position,r.Geometry)))
                    .Where(p=>p.Projection is {Error:<=18,Vertical:<=3})
                    .OrderBy(p=>p.Projection!.Value.Error).Take(2).ToArray();
                bool ambiguous=sign.OwnershipAmbiguous || sign.RoadId is null && (owners.Length==0||owners[0].Road.Geometry.Id!=road.Id
                    || owners.Length>1&&owners[1].Projection!.Value.Error-owners[0].Projection!.Value.Error<2);
                if(owners.Length>0&&owners[0].Road.Geometry.Id!=road.Id)continue;
                var direction=new Vector3(sign.Direction.X,0,sign.Direction.Z);
                float dot=direction.LengthSquared()<.001f?0:Vector3.Dot(Vector3.Normalize(direction),projection.Value.Heading);
                if(dot<-.65f)continue; // sign governs the opposite travel direction
                bool uncertain=ambiguous||dot<.65f||sign.TemplateOverride||sign.Action.Unsupported;
                events.Add((offset+projection.Value.Along,1,uncertain?new(Unsupported:true):sign.Action,null,
                    uncertain?"标牌道路归属、朝向或模板不确定："+sign.Id:
                    "标牌 "+sign.Id+(sign.FollowRoad?"（按道路切向近似自动对齐）":"")));
            }
            offset+=Length(road);
        }
        // Evaluate the exact monitored point too, including a city/area boundary between samples.
        var last=forward.LastOrDefault(r=>matcher.ProjectDistance(camera.MonitoringPosition,camera.TravelDirection,[r]).HasValue);
        var lastMetadata=last is not null&&ulong.TryParse(last.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var lastUid)
            ?data.Roads.GetValueOrDefault(lastUid):null;
        var lastClass=lastMetadata?.SpeedClass;
        var atCamera=BaseAt(camera.MonitoringPosition,lastClass,lastMetadata?.Country);
        events.Add((cameraAt.Value,0,null,atCamera.Kph,atCamera.Reason));
        int? currentBase=null,posted=null;bool? urban=null;bool unknown=false;
        string reason="缺少限速依据";
        foreach(var item in events.OrderBy(e=>e.At).ThenBy(e=>e.Order))
        {
            if(item.Action is null)
            {
                if(item.Base!=currentBase)
                {
                    // Version one treats a default-limit transition as a new local context.
                    bool hadPosted=posted.HasValue;
                    posted=null;urban=null;
                    if(hadPosted){unknown=true;reason="道路/区域默认值改变，既有标牌作用范围待确认";}
                    else if(currentBase.HasValue&&item.Base.HasValue)unknown=false;
                    if(!unknown)reason=item.Reason;
                }
                currentBase=item.Base;
            }
            else if(item.Action.Unsupported){unknown=true;reason=item.Reason;}
            else
            {
                var action=item.Action;
                if(action.Reset){posted=null;unknown=false;}
                if(action.Urban.HasValue)urban=action.Urban;
                if(action.SetKph.HasValue){posted=action.SetKph;unknown=false;}
                reason=item.Reason;
            }
        }
        if(lastMetadata?.CountryUncertain==true)return new(null,"道路国家证据冲突或国界国家ID未知");
        if(unknown)return new(null,reason);
        var country=CountryAt(camera.MonitoringPosition,lastMetadata?.Country);
        if(country is null)return new(null,"国家归属未知");
        if(lastClass is null)return new(null,"监测道路车道类别或变体未支持");
        if(!data.Limits.TryGetValue(country+"/"+lastClass,out var rule))return new(null,"缺少货车限速规则："+country+"/"+lastClass);
        int? baseline=urban.HasValue?(urban.Value?rule.Urban:rule.Rural):currentBase;
        int? limit=posted??baseline;
        if(limit is not >0)return new(null,"道路默认限速或解除后的限速未知");
        if(rule.Maximum>0)limit=Math.Min(limit.Value,rule.Maximum);
        var areaLimits=data.Regions.Where(r=>r.SetKph.HasValue&&r.Contains(camera.MonitoringPosition)).Select(r=>r.SetKph!.Value).Distinct().ToArray();
        if(areaLimits.Length>1)return new(null,"重叠区域的限速存在冲突");
        if(areaLimits.Length==1){limit=Math.Min(limit.Value,areaLimits[0]);reason="交通区域限速";}
        return new(limit,"实验估算："+reason+"；"+country+"/"+lastClass);
    }

    private string? CountryAt(Vector3 point,string? roadCountry=null)
    {
        var countries=data.Regions.Where(r=>r.Country is not null&&r.Contains(point)).Select(r=>r.Country!).Distinct().ToArray();
        if(countries.Length>1)return null;
        string? regionCountry=countries.Length==1?countries[0]:null;
        if(roadCountry is not null&&regionCountry is not null&&roadCountry!=regionCountry)return null;
        return roadCountry??regionCountry??data.Country;
    }
    private SpeedLimitResult BaseAt(Vector3 p,string? cls,string? roadCountry=null)
    {
        string? country=CountryAt(p,roadCountry);
        if(country is null||cls is null||!data.Limits.TryGetValue(country+"/"+cls,out var rule))return new(null,"国家或道路类别未知");
        bool urban=data.Regions.Any(r=>r.Urban&&r.Contains(p));
        int value=urban?rule.Urban:rule.Rural;
        return new(value>0?value:null,urban?"城市道路默认限速":"国家道路默认限速");
    }
    private static double Length(DirectedRoad road)=>road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
    internal static (double Along,float Error,float Vertical,Vector3 Heading)? Nearest(Vector3 point,DirectedRoad road)
    {
        double along=0; (double Along,float Error,float Vertical,Vector3 Heading)? best=null;
        foreach(var(a,b)in road.Points.Zip(road.Points.Skip(1)))
        {
            var delta=b-a;float length=delta.Length();
            if(length<.001f)continue;
            float t=Math.Clamp(Vector3.Dot(point-a,delta)/delta.LengthSquared(),0,1);
            var p=a+delta*t;
            float error=Vector2.Distance(new(point.X,point.Z),new(p.X,p.Z));
            if(best is null||error<best.Value.Error)best=(along+length*t,error,Math.Abs(point.Y-p.Y),Vector3.Normalize(new(delta.X,0,delta.Z)));
            along+=length;
        }
        return best;
    }
}
