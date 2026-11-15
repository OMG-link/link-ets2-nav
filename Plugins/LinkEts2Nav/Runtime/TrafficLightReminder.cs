using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed class TrafficLightReminderOptions
{
    public bool Enabled {get;init;}=true;
    public static TrafficLightReminderOptions Load(string hostDirectory)
    {
        string file=Path.Combine(hostDirectory,"Settings","LinkEts2Nav.TrafficSignals.json");
        if(!File.Exists(file))return new();
        if(new FileInfo(file).Length>65536)throw new InvalidDataException("Traffic signal settings are too large.");
        return JsonSerializer.Deserialize<TrafficLightReminderOptions>(File.ReadAllText(file),new JsonSerializerOptions
        {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})
            ??throw new InvalidDataException("Traffic signal settings are empty.");
    }
}

public sealed record TrafficLightDiagnostic(string State,string? TargetId=null,SignalPhase? Phase=null,
    float? Remaining=null,double? StopDistance=null,SignalPhase? NextPhase=null,bool ClockVerified=false,bool Queued=false);
public sealed record TrafficSignalSelection(TrafficSignalControl Control,TrafficSignalObservation Signal,DateTimeOffset ReceivedAt,double StopDistance);

/// <summary>Matches a controlled PPD curve to its real SDK lamp, never the nearest arbitrary traffic light.</summary>
public sealed class TrafficSignalBinder
{
    public const float MaximumWaitingDistance=25;
    private TruckLib.ScsMap.Map? previousMap;
    private RoadPathSource? paths;
    private IReadOnlyList<DirectedRoad>? previousPaths;
    private NativeLaneGraph? throughTraffic;
    public TrafficSignalSelection? Match(GameDataSnapshot game,NearbyMapSnapshot nearby,NavigationDiagnostic navigation,out string reason)
    {
        reason="等待停止线前的唯一受控车道";
        if(game.Vehicle is not {} vehicle)return null;
        var position=new Vector3((float)vehicle.X,(float)vehicle.Y,(float)vehicle.Z);
        bool routed=game.RouteAvailability==RouteAvailability.Available;
        if(!routed&&game.RouteAvailability!=RouteAvailability.None){reason="导航状态不确定";return null;}
        if(!routed)PrepareUnroutedApproach(nearby);
        var selected=routed?navigation.LaneRoute?.Sections.Select(s=>s.Path).ToArray()??[]:
            throughTraffic!.Edges.SelectMany(p=>p.LanePaths).ToArray();
        var candidates=selected.Select(path=>(Path:path,Control:First(path,nearby.TrafficSignals)))
            .Where(p=>p.Control is not null).Select(p=>(p.Path,Control:p.Control!,Distance:WaitingDistance(p.Control!,position,vehicle.ForwardDirection)))
            .Where(p=>p.Distance is not null).OrderBy(p=>p.Distance).ToArray();
        if(candidates.Length==0)return null;
        var closest=candidates[0];
        // Selected navigation curves may span several future intersections. Only the nearest stop applies.
        if(candidates.Any(c=>c.Control.Id!=closest.Control.Id&&Math.Abs(c.Distance!.Value-closest.Distance!.Value)<1))
        {reason="停止线灯组归属不唯一";return null;}
        if(!routed)
        {
            var approach=paths!.GetPath(position,vehicle.ForwardDirection,game.Route,RouteAvailability.None);
            if(!approach.Any(e=>e.EndNodeUid==closest.Path.EntryNodeUid))
            {reason="无导航时尚未证明必经该路口";return null;}
            var alternatives=throughTraffic!.From(closest.Path.EntryNodeUid).SelectMany(p=>p.LanePaths).ToArray();
            if(alternatives.Length==0||alternatives.Select(p=>First(p,nearby.TrafficSignals)?.Id).Any(id=>id!=closest.Control.Id))
            {reason="无导航且前向延续不共享单一灯组";return null;}
        }
        if(closest.Control.Timing is null){reason=closest.Control.Reason;return null;}
        var matches=game.TrafficSignals.Signals.Where(s=>closest.Control.Lamps.Any(l=>Vector3.Distance(l.Position,s.Position)<=1.5f
            &&Math.Abs(Quaternion.Dot(l.Rotation,s.Rotation))>=.985f)).ToArray();
        if(matches.Length==0){reason="没有匹配的实时灯组位置及朝向";return null;}
        if(matches.Select(s=>s.Phase).Distinct().Count()!=1||matches.Max(s=>s.PhaseRemaining)-matches.Min(s=>s.PhaseRemaining)>.25f)
        {reason="同组实时灯色或倒计时不一致";return null;}
        reason="已绑定必经受控灯组";
        return new(closest.Control,matches.MaxBy(s=>s.PhaseRemaining)!,game.TrafficSignals.ReceivedAt,closest.Distance!.Value);
    }
    private void PrepareUnroutedApproach(NearbyMapSnapshot nearby)
    {
        if(ReferenceEquals(previousMap,nearby.Map)&&ReferenceEquals(previousPaths,nearby.PrefabPaths))return;
        previousMap=nearby.Map;previousPaths=nearby.PrefabPaths;
        throughTraffic=(nearby.AuxiliaryLanes?.Graph??new NativeLaneGraph(nearby.PrefabPaths)).ThroughTraffic;
        paths=new(nearby.Map,nearby.PrefabPaths,native:nearby.AuxiliaryLanes?.Graph,
            playerAccess:nearby.Data is {} data?data.SpeedLimits.RoadPlayerAccess:null);
    }
    private static TrafficSignalControl? First(PrefabLanePath path,TrafficSignalMap controls)
        =>path.CurveIndices.Select(i=>controls.Find(path.PrefabUid,i)).FirstOrDefault(c=>c is not null);
    private static double? WaitingDistance(TrafficSignalControl control,Vector3 position,Vector3 heading)
    {
        var delta=control.StopPoint-position;float vertical=Math.Abs(delta.Y);delta.Y=0;
        float along=Vector3.Dot(delta,control.Forward);
        float lateral=(delta-control.Forward*along).Length();
        return along>=0&&along<=MaximumWaitingDistance&&lateral<=1.8f&&vertical<=3
            &&Vector3.Dot(heading,control.Forward)>=.85f?along:null;
    }
}

public sealed record TrafficLightDecision(TrafficLightDiagnostic Diagnostic,string? NoticeId=null);

/// <summary>One reminder per real red phase, using observed countdown progress rather than a guessed global cycle.</summary>
public sealed class TrafficLightReminderPolicy
{
    public const double StoppedSpeedTolerance=.001;
    private string? session,target;
    private SignalPhase? phase;
    private float? remaining;
    private DateTimeOffset previousAt,progressAt;
    private DateTimeOffset? stoppedAt;
    private bool clockVerified,notified;
    private long cycle;
    public TrafficLightDecision Update(GameDataSnapshot data,TrafficSignalSelection? selection,DateTimeOffset now,string missingReason,bool audioReady=true)
    {
        if(session!=data.SessionId){session=data.SessionId;target=null;phase=null;remaining=null;cycle=0;notified=false;ClearTiming();}
        if(!data.IsVehicleUsable||data.Vehicle is not {SdkActive:true,Paused:false} vehicle)
        {ClearTiming();return new(new("暂停或定位失效，撤销准备提醒"));}
        if(selection is null){ClearTiming();return new(new(missingReason));}
        var signal=selection.Signal;var timing=selection.Control.Timing;
        TrafficLightDiagnostic Diagnostic(string state,bool queued=false)=>new(state,selection.Control.Id,signal.Phase,
            signal.PhaseRemaining,selection.StopDistance,timing?.NextAfterRed,clockVerified,queued);
        if(now<selection.ReceivedAt||now-selection.ReceivedAt>TimeSpan.FromMilliseconds(500)
            ||now<vehicle.ReceivedAt||now-vehicle.ReceivedAt>TimeSpan.FromSeconds(1))
        {ClearTiming();return new(Diagnostic("实时灯色或车辆样本已过期"));}
        string key=selection.Control.Id+":"+timing?.Source;
        bool changed=target!=key;
        bool newRed=signal.Phase==SignalPhase.Red&&(changed||phase!=SignalPhase.Red
            ||remaining is {} old&&signal.PhaseRemaining>old+.5f);
        if(changed){target=key;ClearTiming();notified=false;}
        if(newRed){cycle++;notified=false;ClearTiming();}
        if(previousAt!=default&&selection.ReceivedAt>previousAt&&remaining is {} before&&phase==signal.Phase)
        {
            double seconds=(selection.ReceivedAt-previousAt).TotalSeconds;
            double consumed=before-signal.PhaseRemaining;
            if(seconds>=.05&&consumed>0)
            {
                clockVerified=consumed/seconds is >=.65 and <=1.35;
                if(clockVerified)progressAt=selection.ReceivedAt;
            }
        }
        if(previousAt==default||phase!=signal.Phase||(selection.ReceivedAt-previousAt).TotalSeconds>=.05)
        {previousAt=selection.ReceivedAt;remaining=signal.PhaseRemaining;phase=signal.Phase;}
        if(Math.Abs(vehicle.SpeedMetersPerSecond)>StoppedSpeedTolerance)
        {stoppedAt=null;return new(Diagnostic("车辆仍在移动，不提醒"));}
        stoppedAt??=now;
        if(signal.Phase!=SignalPhase.Red)return new(Diagnostic("当前不是红灯等待相位"));
        // For a customized ordinary lamp the SDK red countdown ends at green or preparatory
        // red/amber. Its exact variant is unknown; never invent it from the unmodified PPD.
        if(timing?.NextAfterRed is not (SignalPhase.Green or SignalPhase.OrangeToGreen)
            &&timing is not {CustomLayout:true,NextAfterRed:null})
            return new(Diagnostic("红灯后的下一相位未知"));
        if(!clockVerified||now-progressAt>TimeSpan.FromMilliseconds(500))return new(Diagnostic("等待验证倒计时按现实秒连续更新"));
        if(now-stoppedAt<TimeSpan.FromMilliseconds(400))return new(Diagnostic("等待确认车辆完全停止"));
        if(notified)return new(Diagnostic("本次红灯等待已提醒"));
        if(signal.PhaseRemaining>3)return new(Diagnostic("红灯尚未进入三秒准备区间"));
        if(!audioReady)return new(Diagnostic("固定提醒音频尚未预热就绪"));
        notified=true;
        return new(Diagnostic("已安排红灯即将变绿提醒",true),selection.Control.Id+":red:"+cycle);
    }
    private void ClearTiming(){previousAt=default;progressAt=default;stoppedAt=null;clockVerified=false;}
}

public sealed class TrafficLightReminderRuntime
{
    public const string Text="红灯即将变绿。";
    public const string Source="traffic-light";
    private readonly TrafficSignalBinder binder=new();
    private readonly TrafficLightReminderPolicy policy=new();
    private readonly GuidanceCoordinator guidance;
    private readonly bool enabled;
    public TrafficLightReminderRuntime(GuidanceCoordinator guidance,TrafficLightReminderOptions options)
    {
        this.guidance=guidance;enabled=options.Enabled;
        if(enabled)guidance.PrepareNotifications(Source,[Text]);
    }
    public TrafficLightDiagnostic Process(GameDataSnapshot data,NearbyMapSnapshot map,NavigationDiagnostic navigation,DateTimeOffset now)
    {
        if(!enabled){guidance.WithdrawNotifications(Source);return new("实验提醒已关闭");}
        string reason="等待车辆及灯组数据";
        var selected=data.IsVehicleUsable?binder.Match(data,map,navigation,out reason):null;
        var decision=policy.Update(data,selected,now,reason,guidance.IsPrepared(Text));
        // A preparation notice expires when the binding, red phase or waiting condition no longer applies.
        if(decision.NoticeId is {} id)guidance.Notify(Source,data.SessionId,new(id,Text));
        else if(decision.Diagnostic.State!="本次红灯等待已提醒")guidance.WithdrawNotifications(Source);
        return decision.Diagnostic;
    }
    public TrafficLightDiagnostic Suspend(GameDataSnapshot data,DateTimeOffset now,string reason)
    {
        guidance.WithdrawNotifications(Source);
        return policy.Update(data,null,now,reason).Diagnostic;
    }
}
