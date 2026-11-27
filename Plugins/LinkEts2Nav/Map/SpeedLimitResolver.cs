using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.Map;

/// <summary>Simulates temporary limits only along the selected player-to-camera path.</summary>
public sealed class SpeedLimitResolver
{
    private readonly record struct RoutePoint(int Visit,double Along,Vector3 Position);
    private readonly SpeedLimitData data;
    private readonly RoadPathSource paths;
    private readonly DirectedPathMatcher matcher=new();
    private readonly Func<DirectedRoad,MapQueryIssue>? missingProjection;
    public SpeedLimitResolver(SpeedLimitData data,RoadPathSource paths,Func<DirectedRoad,MapQueryIssue>? missingProjection=null)
    {
        this.paths=paths;
        this.missingProjection=missingProjection;
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
        if(data.Error is not null)return Unavailable(data.Error,MapQueryFailureKind.DefinitionUnavailable,camera.MonitoringPosition);
        if(route.Count==0||route.Zip(route.Skip(1)).Any(p=>p.First.EndNodeUid!=p.Second.StartNodeUid))
            // The SDK cache may contain distant gaps. Keep their own query
            // anchors; never relabel every cached gap as this camera's demand.
            return Unavailable("到探头的路径尚未连续",MapQueryFailureKind.UnresolvedReference,camera.MonitoringPosition);
        var start=matcher.ProjectDistance(position,heading,route);
        var goal=matcher.ProjectDistance(camera.MonitoringPosition,camera.TravelDirection,route);
        if(start is null||goal is null||goal<start-.01)return Unavailable("当前位置或探头未匹配同一条前进路径",MapQueryFailureKind.Ambiguous,camera.MonitoringPosition);
        int? temporary=currentSdkKph is >0 and <=300?(int)Math.Round(currentSdkKph.Value):null;
        bool unknown=temporary is null;bool? urban=null,motorway=null;
        string reason=temporary.HasValue?"SDK 当前限速初始化":"当前限速种子不可用";
        double offset=0;RoutePoint target=default;RoutePoint? source=null;bool reachedPlayer=false;string? lastSign=null;
        for(int visit=0;visit<route.Count;visit++)
        {
            var road=route[visit];
            double length=RouteGeometry.Length(road);
            if(offset>goal.Value+.01)break;
            if(offset+length>=start.Value-.01)
            {
                if(!reachedPlayer)
                {
                    reachedPlayer=true;
                    source=new(visit,Math.Max(0,start.Value-offset),position);
                }
                if(!data.SignProjections!.Contains(road))return Unavailable("路径区段的标牌投影尚未加载",
                    MapQueryFailureKind.DefinitionUnavailable,camera.MonitoringPosition,missingProjection?.Invoke(road));
                // An observed lane uses the exact native chain from the sign index. Other
                // road geometry can be sampled differently by RoadPathSource and the loader.
                var events=data.SignProjections.On(road).Select(sign=>(Sign:sign,
                    At:road.ObservedLane is null?SpeedLimitGeometry.Nearest(sign.Position,road)?.Along:sign.AlongMeters))
                    .Where(p=>p.At.HasValue).OrderBy(p=>p.At).ThenBy(p=>p.Sign.SignId,StringComparer.Ordinal);
                foreach(var item in events)
                {
                    double at=offset+item.At!.Value;
                    if(at<=start.Value+.01||at>goal.Value+.01||lastSign==item.Sign.SignId)continue;
                    lastSign=item.Sign.SignId;var action=item.Sign.Action;
                    if(action.Unsupported){unknown=true;reason="路径标牌未确认："+lastSign;continue;}
                    // There is one active temporary value, not a stack. One reset clears it all.
                    if(action.Reset){temporary=null;unknown=false;}
                    if((action.Urban.HasValue&&action.Urban!=urban)||(action.Motorway.HasValue&&action.Motorway!=motorway))source=null;
                    if(action.Urban.HasValue)urban=action.Urban;
                    if(action.Motorway.HasValue)motorway=action.Motorway;
                    if(action.SetKph.HasValue){temporary=action.SetKph;unknown=false;source=new(visit,item.At.Value,item.Sign.Position);}
                    reason=(action.Reset&&!action.SetKph.HasValue?"解除标牌 ":"路径标牌 ")+lastSign;
                }
            }
            target=new(visit,Math.Clamp(goal.Value-offset,0,length),camera.MonitoringPosition);offset+=length;
        }
        if(unknown)return Unavailable(reason,MapQueryFailureKind.DefinitionUnavailable,camera.MonitoringPosition);
        return Compose(route,source,target,temporary,urban,motorway,reason);
    }
    private SpeedLimitResult Unavailable(string reason,MapQueryFailureKind kind,Vector3 queryAnchor,MapQueryIssue? issue=null)
    {
        var problem=(issue??new MapQueryFailure(kind,reason)) with{QueryAnchor=queryAnchor};
        paths.ReportMapIssue(problem);
        return new(null,reason){MapIssue=problem};
    }
    private SpeedLimitResult Compose(IReadOnlyList<DirectedRoad> route,RoutePoint? source,RoutePoint target,
        int? temporary,bool? urbanOverride,bool? motorway,string reason)
    {
        var position=target.Position;var road=route[target.Visit];
        var metadata=data.SignProjections!.Context(road);
        var regions=data.RegionIndex!.At(position);
        if(metadata is null)return Unavailable("Road country metadata is unavailable",MapQueryFailureKind.DefinitionUnavailable,position);
        var resolved=metadata.QueryCountry();
        if(resolved.Issue is {} issue&&issue is not UndeterminedRoadCountry)return Unavailable(issue.Reason,MapQueryFailureKind.DefinitionUnavailable,position,issue);
        string? country=resolved.Value;
        if(regions.Any(r=>r.Unsupported))return Unavailable("所在交通区域包含未支持的限速规则",MapQueryFailureKind.DefinitionUnavailable,position);
        string? speedClass=metadata?.ClassFor(road);
        if(motorway==true)speedClass="motorway";
        if(speedClass is null)return Unavailable("监测道路车道类别或变体未支持",MapQueryFailureKind.DefinitionUnavailable,position);
        if(!TruckSpeedLimitRules.TryGet(data.Limits,country,speedClass,out var rule,out bool usedDefault))
            return Unavailable("缺少货车限速规则："+country+"/"+speedClass,MapQueryFailureKind.DefinitionUnavailable,position);
        if(usedDefault)reason+="；"+TruckSpeedLimitRules.DefaultReasonFor(country,speedClass);
        bool urban=urbanOverride??regions.Any(r=>r.Urban);
        int baseline=urban?rule.Urban:rule.Rural;
        int? value=temporary??baseline;
        if(value is not >0)return Unavailable("道路默认限速或解除后的限速未知",MapQueryFailureKind.DefinitionUnavailable,position);
        if(rule.Maximum>0&&value>=rule.Maximum){value=rule.Maximum;reason+="；货车最高限速";}
        int? area=regions.Where(r=>r.SetKph is >0).Min(r=>r.SetKph);
        if(area.HasValue){value=Math.Min(value.Value,area.Value);reason+="；交通区域限速";}
        bool useDefault=temporary is null;
        if(baseline>0&&value>baseline)
        {
            if(source is {} from&&country is not null&&SameEnvironment(route,from,target,country,speedClass,urban,urbanOverride,motorway))
                reason+="；沿途限速环境一致";
            else {value=baseline;useDefault=true;}
        }
        if(useDefault&&baseline>0)reason+="；"+(urban?"城区默认限速":"国家道路默认限速");
        return new(value,"实验估算："+reason+"；"+country+"/"+speedClass);
    }

    private bool SameEnvironment(IReadOnlyList<DirectedRoad> route,RoutePoint source,RoutePoint target,
        string country,string speedClass,bool urban,bool? urbanOverride,bool? motorway)
    {
        var regions=data.RegionIndex!;
        if(urbanOverride is null&&regions.At(source.Position).Any(r=>r.Urban)!=urban)return false;
        for(int visit=source.Visit;visit<=target.Visit;visit++)
        {
            var road=route[visit];var metadata=data.SignProjections!.Context(road);
            if(metadata?.QueryCountry().Value!=country||(motorway!=true&&metadata.ClassFor(road)!=speedClass))return false;
            if(urbanOverride.HasValue)continue;
            double from=visit==source.Visit?source.Along:0;
            double to=visit==target.Visit?target.Along:double.PositiveInfinity;
            double along=0;
            for(int i=1;i<road.Points.Count&&along<to;i++)
            {
                if(road.SegmentStarts.Contains(i))continue;
                var a=road.Points[i-1];var b=road.Points[i];double length=Vector3.Distance(a,b);
                if(length>0&&along+length>from)
                {
                    var first=Vector3.Lerp(a,b,(float)Math.Max(0,(from-along)/length));
                    var last=Vector3.Lerp(a,b,(float)Math.Min(1,(to-along)/length));
                    if(!regions.MatchesUrbanAlong(first,last,urban))return false;
                }
                along+=length;
            }
        }
        return true;
    }
}
