using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Map;

/// <summary>
/// Urban sign transitions on the confirmed directed route. Only actually passed
/// signs update persistent state; future signs are simulated on a local copy.
/// Polygon membership never creates an entry or supplies an assumed initial state.
/// </summary>
public sealed class CityEntryEventBuilder(Action<string>? log=null)
{
    public const string Kind="city-entry";
    private sealed record Landmark(string SignId,string RoadId,ulong From,ulong To,
        double Along,Vector3 Position,bool? Urban);
    private bool? actualUrban;
    private double? actualStateAlong;
    private string? diagnosticSignature;
    private SpeedLimitData? projectionSource,cachedProjection;
    private IReadOnlyList<DirectedRoad>? projectionGeometry;

    public void Reset()
    {
        actualUrban=null;actualStateAlong=null;diagnosticSignature=null;
        projectionSource=cachedProjection=null;projectionGeometry=null;
    }
    /// <summary>A new route coordinate frame does not change the actual observed urban state.</summary>
    public void Rebase()=>actualStateAlong=null;

    /// <param name="routeOriginAlongMeters">Canonical coordinate of route[0].Points[0],
    /// kept stable when trimming a driven prefix.</param>
    /// <param name="projectionPrefabPaths">Full cached snapshot PPD paths, used only if
    /// the data has no existing sign index. Production scanner output already has one.</param>
    public IReadOnlyList<EnvironmentPreviewEvent> Build(IReadOnlyList<DirectedRoad> route,
        double playerAlongMeters,SpeedLimitData data,double routeOriginAlongMeters=0,
        IReadOnlyList<DirectedRoad>? projectionPrefabPaths=null)
    {
        if(!double.IsFinite(playerAlongMeters)||!double.IsFinite(routeOriginAlongMeters))
            throw new ArgumentOutOfRangeException(nameof(playerAlongMeters));
        var warnings=new List<string>();
        if(data.Error is not null)
        {Report(["入城标牌暂不可用："+data.Error]);return [];}
        var projected=Projection(data,projectionPrefabPaths??route);
        var originals=data.Signs.DistinctBy(s=>s.Id).ToDictionary(s=>s.Id,StringComparer.Ordinal);
        var landmarks=new List<Landmark>();double along=routeOriginAlongMeters;
        foreach(var road in route)
        {
            double length=road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            foreach(var sign in projected.SignProjections!.On(road))
            {
                originals.TryGetValue(sign.SignId,out var original);
                bool relevant=sign.Action.Urban.HasValue||original?.Action.Urban.HasValue==true;
                if(!relevant)continue;
                // The cache and current route may use different sampling densities.
                // Reuse the cached world point, not another polyline's local distance.
                var at=SpeedLimitGeometry.Nearest(sign.Position,road);
                if(at is not {Error:<=18,Vertical:<=3})
                {warnings.Add("入城标牌未采用："+sign.SignId+"；挂点未匹配当前路线几何。");continue;}
                bool reliable=!sign.Action.Unsupported&&original?.TemplateOverride!=true&&original?.OwnershipAmbiguous!=true;
                if(!reliable)warnings.Add("入城标牌未采用："+sign.SignId+"；规则、模板、归属或方向未确认。");
                landmarks.Add(new(sign.SignId,road.Id,road.StartNodeUid,road.EndNodeUid,
                    along+at.Value.Along,sign.Position,reliable?sign.Action.Urban:null));
            }
            along+=length;
        }
        var onRoute=landmarks.Select(s=>s.SignId).ToHashSet(StringComparer.Ordinal);
        foreach(var sign in originals.Values.Where(s=>s.Action.Urban.HasValue&&!onRoute.Contains(s.Id)))
            foreach(string issue in projected.Diagnostics.Where(d=>d.Contains(sign.Id,StringComparison.Ordinal)))
                warnings.Add("入城标牌未采用："+issue);
        Report(warnings);
        var ordered=landmarks.OrderBy(s=>s.Along).ThenBy(s=>s.SignId,StringComparer.Ordinal).ToArray();
        var passed=ordered.LastOrDefault(s=>s.Along<playerAlongMeters);
        if(passed is not null&&(actualStateAlong is null||passed.Along>=actualStateAlong))
        {actualUrban=passed.Urban;actualStateAlong=passed.Along;}
        bool? forecast=actualUrban;var result=new List<EnvironmentPreviewEvent>();
        foreach(var sign in ordered.Where(s=>s.Along>=playerAlongMeters))
        {
            if(sign.Urban==true&&forecast!=true)
                result.Add(new(Identity(sign),Kind,sign.Along,sign.Along,Text));
            forecast=sign.Urban;
        }
        return result;
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
    public static string Text(int meters)=>meters<50?"即将进入城区，请注意减速慢行。":
        NavigationPrompts.AtDistance(ManeuverKind.Straight,meters,0,"进入城区，请注意减速慢行");

    // Parallel boards can describe the same directed cross-section. Physical
    // position, directed edge and canonical occurrence identify the landmark,
    // rather than whichever of those boards happened to be discovered first.
    private static string Identity(Landmark sign)=>string.Create(CultureInfo.InvariantCulture,
        $"city-entry:{sign.RoadId}:{sign.From:X16}:{sign.To:X16}:{sign.Position.X:F1}:{sign.Position.Z:F1}:{sign.Along:F1}");
}
