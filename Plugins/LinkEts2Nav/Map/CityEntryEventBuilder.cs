using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Map;

/// <summary>
/// Each confirmed urban entry sign on the directed route marks a physical entrance.
/// The same town may have multiple entrances without an intervening signed exit.
/// </summary>
public sealed class CityEntryEventBuilder(Action<string>? log=null,IReadOnlySet<string>? spokenCityNames=null)
{
    public const string Kind="city-entry";
    private sealed record Landmark(string SignId,string RoadId,ulong From,ulong To,
        double Along,Vector3 Position,float? Side,string? CityName,int Visit)
    {
        public string? EventId {get;set;}
    }
    private string? diagnosticSignature;
    private SpeedLimitData? projectionSource,cachedProjection;
    private IReadOnlyList<DirectedRoad>? projectionGeometry;
    private Landmark[] currentLandmarks=[];

    public void Reset()
    {
        diagnosticSignature=null;
        projectionSource=cachedProjection=null;projectionGeometry=null;currentLandmarks=[];
    }

    /// <param name="routeOriginAlongMeters">Canonical coordinate of route[0].Points[0],
    /// kept stable when trimming a driven prefix.</param>
    /// <param name="projectionPrefabPaths">Full cached snapshot PPD paths, used only if
    /// the data has no existing sign index. Production scanner output already has one.</param>
    public IReadOnlyList<EnvironmentPreviewEvent> Build(IReadOnlyList<DirectedRoad> route,
        double playerAlongMeters,SpeedLimitData data,double routeOriginAlongMeters=0,
        IReadOnlyList<DirectedRoad>? projectionPrefabPaths=null)
        =>Build(route,playerAlongMeters,data,routeOriginAlongMeters,projectionPrefabPaths,null,out _);

    internal IReadOnlyList<EnvironmentPreviewEvent> Build(IReadOnlyList<DirectedRoad> route,
        double playerAlongMeters,SpeedLimitData data,double routeOriginAlongMeters,
        IReadOnlyList<DirectedRoad>? projectionPrefabPaths,IReadOnlyList<DirectedRoad>? previousVisits,out IReadOnlyList<string> removed)
    {
        if(!double.IsFinite(playerAlongMeters)||!double.IsFinite(routeOriginAlongMeters))
            throw new ArgumentOutOfRangeException(nameof(playerAlongMeters));
        var warnings=new List<string>();removed=[];
        if(data.Error is not null)
        {Report(["入城标牌暂不可用："+data.Error]);return [];}
        var projected=Projection(data,projectionPrefabPaths??route);
        var originals=data.Signs.DistinctBy(s=>s.Id).ToDictionary(s=>s.Id,StringComparer.Ordinal);
        var landmarks=new List<Landmark>();
        var onRoute=new HashSet<string>(StringComparer.Ordinal);
        double along=routeOriginAlongMeters;
        for(int visit=0;visit<route.Count;visit++)
        {
            var road=route[visit];
            double length=road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            foreach(var sign in projected.SignProjections!.On(road))
            {
                originals.TryGetValue(sign.SignId,out var original);
                if(sign.Action.Urban!=true&&original?.Action.Urban!=true)continue;
                // The cache and current route may use different sampling densities.
                // Reuse the cached world point, not another polyline's local distance.
                var at=SpeedLimitGeometry.Nearest(sign.Position,road);
                if(at is not {Error:<=18,Vertical:<=3})
                {warnings.Add("入城标牌未采用："+sign.SignId+"；挂点未匹配当前路线几何。");continue;}
                onRoute.Add(sign.SignId);
                if(sign.Action.Urban!=true||sign.Action.Unsupported||original?.TemplateOverride==true||original?.OwnershipAmbiguous==true)
                {warnings.Add("入城标牌未采用："+sign.SignId+"；规则、模板、归属或方向未确认。");continue;}
                float? side=original is null?null:Vector3.Cross(at.Value.Heading,original.Position-sign.Position).Y;
                landmarks.Add(new(sign.SignId,road.Id,road.StartNodeUid,road.EndNodeUid,
                    along+(road.ObservedLane is null?at.Value.Along:sign.AlongMeters),sign.Position,
                    side,original?.CityName,visit));
            }
            along+=length;
        }
        foreach(var sign in originals.Values.Where(s=>s.Action.Urban==true&&!onRoute.Contains(s.Id)))
            foreach(string issue in projected.Diagnostics.Where(d=>d.Contains(sign.Id,StringComparison.Ordinal)))
                warnings.Add("入城标牌未采用："+issue);
        Report(warnings);
        if(previousVisits is not null)
            removed=ReconcileObservedVisits(previousVisits,route,landmarks,projected.SignProjections!,playerAlongMeters);
        currentLandmarks=landmarks.OrderBy(s=>s.Along).ThenBy(s=>s.SignId,StringComparer.Ordinal).ToArray();
        var stations=new List<List<Landmark>>();
        foreach(var sign in currentLandmarks)
        {
            List<Landmark>? pair=null;
            for(int i=stations.Count-1;i>=0;i--)
            {
                var first=stations[i][0];
                if(sign.Along-first.Along>SignProjector.InterfaceToleranceMeters)break;
                if(sign.Visit==first.Visit&&SameCrossSection(first,sign)
                    &&projected.SignProjections!.ShareNativeStation(route[sign.Visit],first.SignId,sign.SignId))
                {pair=stations[i];break;}
            }
            if(pair is null)stations.Add([sign]);else pair.Add(sign);
        }
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var result=new List<EnvironmentPreviewEvent>();
        foreach(var station in stations)
        {
            var sign=station[0];
            string id=station.FirstOrDefault(s=>s.EventId is not null)?.EventId??Identity(sign);
            // Away from a common lane, different native branches can have the same station on the SDK reference line.
            if(!ids.Add(id))id+=":"+sign.SignId;
            foreach(var board in station)board.EventId=id;
            if(sign.Along>=playerAlongMeters)
                result.Add(new(id,Kind,sign.Along,sign.Along,meters=>Text(meters,sign.CityName is {} name&&spokenCityNames?.Contains(name)==true?name:null)));
        }
        if(removed.Count>0)removed=removed.Except(result.Select(e=>e.Id),StringComparer.Ordinal).ToArray();
        return result;
    }

    private static bool SameCrossSection(Landmark a,Landmark b)
    {
        if(Identity(a)==Identity(b))return true;
        if(a.Side is not {} left||b.Side is not {} right)return false;
        // A divided road can put its reference line on a shoulder. Require the physical poles
        // to span that line (within sign projection accuracy) as well as a shared route station.
        const float tolerance=SignProjector.InterfaceToleranceMeters;
        return Math.Abs(left-right)>tolerance&&Math.Min(left,right)<=tolerance&&Math.Max(left,right)>=-tolerance;
    }

    private IReadOnlyList<string> ReconcileObservedVisits(IReadOnlyList<DirectedRoad> previous,IReadOnlyList<DirectedRoad> route,
        List<Landmark> landmarks,ProjectedSignIndex signs,double playerAlong)
    {
        var removed=new List<string>();
        foreach(var old in currentLandmarks)
        {
            if(old.EventId is not {} id)continue;
            var edge=route[old.Visit];var prior=previous[old.Visit];
            if(edge.ObservedLane is null||!ReferenceEquals(edge.LanePaths,prior.LanePaths)||!signs.Contains(edge))continue;
            // Retain the identity of the same installed sign when its native station changes.
            if(landmarks.Find(s=>s.Visit==old.Visit&&s.SignId==old.SignId) is {} retained)retained.EventId=id;
            else if(old.Along>playerAlong&&!ReferenceEquals(prior.ObservedLane,edge.ObservedLane)
                &&signs.On(prior).Any(s=>s.SignId==old.SignId)&&!signs.On(edge).Any(s=>s.SignId==old.SignId))removed.Add(id);
        }
        return removed;
    }

    private SpeedLimitData Projection(SpeedLimitData data,IReadOnlyList<DirectedRoad> geometry)
    {
        if(data.SignProjections is not null)return data;
        if(!ReferenceEquals(data,projectionSource)||!ReferenceEquals(geometry,projectionGeometry))
        {projectionSource=data;projectionGeometry=geometry;cachedProjection=ProjectedSignBuilder.Build(data,geometry);}
        return cachedProjection!;
    }
    private void Report(IReadOnlyList<string> warnings)
    {
        string signature=string.Join("|",warnings.Distinct().Order(StringComparer.Ordinal));
        if(signature==diagnosticSignature)return;
        diagnosticSignature=signature;
        foreach(string warning in warnings.Distinct())log?.Invoke(warning);
    }
    public static string Text(int meters,string? cityName=null)
    {
        string action="进入"+cityName+"城区，请注意减速慢行";
        return meters<50?"即将"+action+"。":NavigationPrompts.AtDistance(ManeuverKind.Straight,meters,0,action);
    }

    // Parallel boards can describe the same directed cross-section. Physical
    // position, directed edge and canonical occurrence identify the landmark,
    // rather than whichever of those boards happened to be discovered first.
    private static string Identity(Landmark sign)=>string.Create(CultureInfo.InvariantCulture,
        $"city-entry:{sign.RoadId}:{sign.From:X16}:{sign.To:X16}:{sign.Position.X:F1}:{sign.Position.Z:F1}:{sign.Along:F1}");
}
