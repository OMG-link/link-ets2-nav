using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Query prebuilt posted intervals and the nearest four cached area sectors.</summary>
public sealed class SpeedLimitResolver
{
    private readonly SpeedLimitData data;
    public SpeedLimitResolver(SpeedLimitData data,RoadPathSource paths)
    {
        // Production scanner publishes prepared data. This fallback also supports standalone tools/tests.
        this.data=data.PostedLimits is not null||data.Error is not null?data:
            PostedLimitBuilder.Build(data,paths.GetNavigationGraph().Where(e=>e.Id.StartsWith("prefab:")).ToArray());
    }
    public SpeedLimitResult Resolve(MonitoredCamera camera)=>ResolvePosition(camera.MonitoringPosition,camera.TravelDirection);
    public SpeedLimitResult ResolvePosition(Vector3 position,Vector3 direction)
    {
        if(data.Error is not null)return new(null,data.Error);
        var location=data.PostedLimits!.Locate(position,direction);
        if(location is null)return new(null,"监测点未唯一匹配道路");
        var metadata=location.Metadata;
        if(metadata?.CountryUncertain==true)return new(null,"道路国家证据冲突或国界国家ID未知");
        var regions=data.RegionIndex!.At(position);
        var countries=regions.Where(r=>r.Country is not null).Select(r=>r.Country!).Distinct().ToArray();
        if(countries.Length>1 || countries.Length==1&&metadata?.Country is {} rc&&rc!=countries[0])
            return new(null,"道路国家证据冲突或国界国家ID未知");
        string? country=metadata?.Country??countries.SingleOrDefault()??data.Country;
        if(country is null)return new(null,"国家归属未知");
        var posted=location.Posted;
        if(posted.Unknown)return new(null,"标牌临时限速区间未确认或存在汇合冲突");
        if(regions.Any(r=>r.Unsupported))return new(null,"所在交通区域包含未支持的限速规则");
        string? speedClass=metadata?.ClassFor(location.Road);
        if(posted.Motorway==true)speedClass="motorway";
        if(speedClass is null)return new(null,"监测道路车道类别或变体未支持");
        if(!data.Limits.TryGetValue(country+"/"+speedClass,out var rule))
            return new(null,"缺少货车限速规则："+country+"/"+speedClass);
        bool urban=posted.Urban??regions.Any(r=>r.Urban);
        int baseline=urban?rule.Urban:rule.Rural;
        // SCS permits a sign to raise the urban default; max_limit is the non-overridable cap.
        int? value=posted.Kph??(baseline>0?baseline:null);
        if(value is not >0)return new(null,"道路默认限速或解除后的限速未知");
        if(rule.Maximum>0)value=Math.Min(value.Value,rule.Maximum);
        var area=regions.Where(r=>r.SetKph is >0).Select(r=>r.SetKph!.Value).ToArray();
        if(area.Length>0)value=Math.Min(value.Value,area.Min());
        string reason=posted.Kph.HasValue?"标牌区间":urban?"交通区域/标牌城区默认限速":"国家道路默认限速";
        if(area.Length>0)reason+="；交通区域限速";
        return new(value,"实验估算："+reason+"；"+country+"/"+speedClass);
    }
}
