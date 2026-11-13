using LinkEts2Nav.Map;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.Runtime;

public sealed record GuidancePointDiagnostic(string EventId,string TriggerId,string Text,double RemainingMeters,bool Enabled);
public sealed record GuidanceSequenceEndDiagnostic(string Source,string SequenceId,double? RemainingMeters,bool ObservedPassage);
public sealed record GuidanceDiagnostic(int EventCount,int TriggerCount,
    IReadOnlyList<QueuedGuidanceSpeech> Pending,IReadOnlyList<GuidanceTriggerResult> LatestTriggers,string? LastPlayed)
{
    public IReadOnlyList<GuidancePointDiagnostic> Upcoming {get;init;}=[];
    public IReadOnlyList<GuidanceSequenceEndDiagnostic> SequenceEnds {get;init;}=[];
}

/// <summary>Single bridge between the event table, trigger scheduler, synthesis cache and playback owner.</summary>
public sealed class GuidanceCoordinator
{
    private readonly SpeechAnnouncementQueue? cache;
    private readonly GuidancePlaybackManager? playback;
    private readonly Action<string> log;
    private readonly object gate=new();
    private readonly EnvironmentPreviewPlanner environment=new();
    private readonly NavigationJourneyTracker journey=new();
    private string? environmentFrame;
    private string? session,lastPlayed;
    private readonly Dictionary<string,string> playedBySource=new(StringComparer.Ordinal);
    private IReadOnlyList<GuidanceTriggerResult> latest=[];
    private RouteSnapshot? observedRoute;
    private string? replacementRevision;
    private readonly HashSet<string> replacementApplied=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> lastQueued=new(StringComparer.Ordinal);
    private readonly HashSet<(string Source,string Id)> notifications=[];
    public GuidanceTimeline Timeline { get; }=new();
    public SpeechTriggerScheduler Scheduler { get; }=new();
    public event Action<QueuedGuidanceSpeech>? Queued;
    public GuidanceCoordinator(SpeechAnnouncementQueue? cache=null,GuidancePlaybackManager? playback=null,Action<string>? log=null)
    {
        this.cache=cache;this.playback=playback;this.log=log??(_=>{});
        if(playback is not null)playback.Played+=speech=>{lock(gate){lastPlayed=speech.Text;playedBySource[speech.Source]=speech.Text;}};
    }
    public string? LastPlayed(string source){lock(gate)return playedBySource.GetValueOrDefault(source);}
    public string? LastQueued(string source)=>lastQueued.GetValueOrDefault(source);
    public GuidanceDiagnostic Diagnostic
    {get{lock(gate)return new(Timeline.Sources.Sum(p=>p.Events.Count),Timeline.Sources.Sum(p=>p.Triggers.Count),playback?.Pending??[],latest,lastPlayed){Upcoming=Timeline.Sources.SelectMany(p=>p.Triggers
        .Where(t=>!Scheduler.IsConsumed(p.Source,t.Id)&&!t.Silent)
        .Select(t=>new GuidancePointDiagnostic(t.EventId,t.Id,t.Text,t.AlongMeters-p.PlayerAlongMeters,t.Enabled)))
        .OrderBy(p=>p.RemainingMeters).Take(64).ToArray(),SequenceEnds=Timeline.Sources.SelectMany(p=>p.Sequences
            .Select(s=>new GuidanceSequenceEndDiagnostic(p.Source,s.Id,s.EndAlongMeters-p.PlayerAlongMeters,s.Ended))).ToArray()};}}
    public bool WillReplaceSource(string source,string revision)=>replacementRevision==revision&&!replacementApplied.Contains(source);
    public void Notify(string source,string sessionId,GuidanceNotification notification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Text);
        EnsureSession(sessionId);
        if(!notifications.Add((source,notification.Id)))return;
        lastQueued[source]=notification.Text;
        string id="notification:"+notification.Id;
        log("即时语音入队："+notification.Text+" 事件="+id);
        playback?.Notify(source,notification);
        Queued?.Invoke(new(source,id,id,0,notification.Priority,notification.Text){IsNotification=true});
    }
    public void WithdrawNotifications(string source)
        =>playback?.Withdraw(source,playback.Pending.Where(p=>p.Source==source&&p.IsNotification).Select(p=>p.EventId));
    public void Publish(GuidanceSourcePlan plan)
    {
        EnsureSession(plan.SessionId);playback?.BeginUpdate();
        try
        {
        var previous=Timeline.Sources.FirstOrDefault(p=>p.Source==plan.Source);
        if(previous is not null&&replacementRevision==plan.RouteRevision&&replacementApplied.Add(plan.Source))
        {
            var current=plan.Events.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
            var removed=previous.Events.Where(e=>!current.Contains(e.Id)&&e.AlongMeters>previous.PlayerAlongMeters)
                .Select(e=>e.Id).ToArray();
            playback?.Withdraw(plan.Source,removed);
            Scheduler.ForgetEvents(plan.Source,removed);
            // Do not catch up an old turn at a junction whose chosen exit just changed.
            plan=plan with{CoordinateFrame=null};
        }
        Timeline.Publish(plan);playback?.Refresh(plan.Source,plan.Triggers);
        }
        catch(Exception ex)
        {
            playback?.ApplyBatch([],false);
            log("Invalid speech series: "+ex.Message);
            throw;
        }
    }
    public void PublishLandmarks(WarningSnapshot snapshot,IReadOnlyList<PlannedCameraWarning> plans,
        IReadOnlyList<CameraPassage>? passages=null,double? cameraPlayerAlong=null,string? cameraFrame=null,
        IReadOnlyList<CameraObservation>? observations=null)
    {
        EnsureSession(snapshot.SessionId);
        var route=Timeline.Sources.FirstOrDefault(p=>p.Source=="navigation"&&p.Active
            &&p.ObservedAt==snapshot.ObservedAt&&p.CoordinateFrame is not null);
        double playerAlong=route?.PlayerAlongMeters??cameraPlayerAlong??0;
        string? frame=route?.CoordinateFrame??cameraFrame;
        var input=new List<EnvironmentPreviewEvent>();
        var kinds=new Dictionary<string,WarningDistanceKind>();
        foreach(var group in plans.GroupBy(p=>(p.CameraId,p.Kind,p.EncounterRevision)))
        {
            var sample=group.First();string id=$"landmark:{sample.CameraId}:{sample.Kind}:{sample.EncounterRevision}";
            bool road=frame is not null&&sample.DistanceKind==WarningDistanceKind.Road;
            // Preserve the producer's route-length lower bound across unknown map
            // gaps; a physically close camera may still lie after a long detour.
            double distance=Math.Max(sample.DistanceMeters,group.Where(p=>p.DistanceUntilTriggerMeters>0)
                .Select(p=>p.StageDistanceMeters+p.DistanceUntilTriggerMeters).DefaultIfEmpty(0).Max());
            input.Add(new(id,CameraChain(sample.Kind,sample.CameraId),playerAlong+sample.DistanceMeters,road?playerAlong+sample.DistanceMeters:null,
                meters=>PlannedSpeechAnnouncer.DistanceText(meters,sample.SpeedLimitKph,sample.Kind),
                DynamicDistance:!road,IncludeTwoHundred:sample.Kind==CameraWarningKind.Calibrated,DistanceLowerBoundMeters:distance,
                PhysicalEnd:Boundary(sample.CameraId,sample.Kind)));
            kinds[id]=sample.DistanceKind;
        }
        foreach(var passage in passages??[])
        {
            string id=$"landmark:{passage.CameraId}:{passage.Kind}:{passage.EncounterRevision}";
            input.RemoveAll(e=>e.Id==id);
            input.Add(new(id,CameraChain(passage.Kind,passage.CameraId),playerAlong,null,_=>"",Ended:true,PhysicalEnd:Boundary(passage.CameraId,passage.Kind)));
        }
        if(environmentFrame!=frame){environment.Rebase(input);environmentFrame=frame;}
        if(WillReplaceSource("landmarks",snapshot.RouteRevision))
            environment.Reconcile(input.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,Scheduler,"landmarks");
        var plan=environment.Build("landmarks",playerAlong,input,Scheduler,snapshot.IsActive);
        Publish(new("landmarks",snapshot.SessionId,snapshot.RouteRevision,playerAlong,snapshot.IsActive,snapshot.ObservedAt,
            plan.Sequences.Select(s=>new SpeechSequence(s.Id,s.End,
                s.Events.Select(e=>e with{DistanceKind=kinds.GetValueOrDefault(e.Id)}).ToArray(),s.Triggers,s.Ended)).ToArray())
            {CoordinateFrame=frame});
        SpeechEndBoundary? Boundary(string id,CameraWarningKind kind)
        {
            var camera=observations?.FirstOrDefault(c=>c.Id==id);
            if(camera is null)return null;
            return kind==CameraWarningKind.Calibrated
                ?new CameraSpeechEnd(camera.MonitoredPosition,camera.TravelDirection)
                :new CameraSpeechEnd(camera.LocatorPosition);
        }
    }
    // Unknown proximity notices have no proven route order or monitoring boundary;
    // they cannot be predecessors of calibrated road-camera events or each other.
    private static string CameraChain(CameraWarningKind kind,string cameraId)
        =>kind==CameraWarningKind.Calibrated?"camera":"uncalibrated-proximity:"+cameraId;
    public void ClearSource(string source)
    {
        var ids=Timeline.Sources.FirstOrDefault(p=>p.Source==source)?.Events.Select(e=>e.Id).ToArray()??[];
        playback?.Withdraw(source,ids);Scheduler.ForgetEvents(source,ids);Timeline.Clear(source);
    }
    public void Suspend(string source)=>Timeline.Suspend(source);
    public void Observe(GameDataSnapshot data,DateTimeOffset now,bool processJourney=true)
    {
        EnsureSession(data.SessionId);playback?.BeginUpdate();
        if(observedRoute is not null&&observedRoute.Revision!=data.Route.Revision)
        {
            replacementRevision=RouteContinuity.IsForwardExtension(observedRoute,data.Route)?null:data.Route.Revision;
            replacementApplied.Clear();
        }
        observedRoute=data.Route;
        // Playback is released only after the whole trigger batch has been applied.
        playback?.SetActive(data.IsVehicleUsable&&data.Vehicle is {Paused:false,SdkActive:true,IsFinite:true,SpeedMetersPerSecond:>=-.1} v
            &&now>=v.ReceivedAt&&now-v.ReceivedAt<=TimeSpan.FromSeconds(2));
        if(!processJourney)return;
        var lifecycle=journey.Update(data,now);
        if(lifecycle.WithdrawNotices)WithdrawNotifications(NavigationJourneyTracker.Source);
        if(lifecycle.Ended){ClearSource("navigation");ClearSource("city-entry");}
        foreach(var notice in lifecycle.Notifications)Notify(NavigationJourneyTracker.Source,data.SessionId,notice);
    }
    public void Tick(GameDataSnapshot data,DateTimeOffset now)
    {
        EnsureSession(data.SessionId);
        bool active=data.IsVehicleUsable&&data.Vehicle is {SdkActive:true,Paused:false,IsFinite:true,SpeedMetersPerSecond:>=-.1} v
            &&now>=v.ReceivedAt&&now-v.ReceivedAt<=TimeSpan.FromSeconds(2);
        playback?.SetActive(active);
        if(!active){cache?.ClearPlan("guidance-preparation");latest=[];playback?.ApplyBatch([],false);return;}
        latest=[];
        try
        {
        latest=Scheduler.Update(Timeline,now,data.Vehicle!.SpeedMetersPerSecond);
        cache?.UpdatePlan("guidance-preparation",Scheduler.Prepare(Timeline,now));
        foreach(var result in latest)
        {
            if(result.Disposition==TriggerDisposition.Ended){log("语音系列结束："+result.Event.SequenceId);continue;}
            if(result.Disposition!=TriggerDisposition.Queued)continue;
            var t=result.Trigger;
            var item=new QueuedGuidanceSpeech(result.Source,result.Event.Id,t.Id,t.SequenceOrder,t.Priority,t.Text){SpeechSequenceId=result.Event.SequenceId};
            lastQueued[result.Source]=t.Text;
            log("语音触发入队："+t.Text+" 事件="+result.Event.Id+" 触发点="+t.Id);
            Queued?.Invoke(item);
        }
        }
        finally {playback?.ApplyBatch(latest,active);}
    }
    private void EnsureSession(string value)
    {if(session!=value){Reset();session=value;}}
    public void Reset()
    {
        Timeline.Reset();Scheduler.Reset();journey.Reset();playback?.Reset();cache?.ClearPlan("guidance-preparation");
        latest=[];lastQueued.Clear();notifications.Clear();environment.Reset();environmentFrame=null;session=null;observedRoute=null;replacementRevision=null;replacementApplied.Clear();lock(gate){lastPlayed=null;playedBySource.Clear();}
    }
}
