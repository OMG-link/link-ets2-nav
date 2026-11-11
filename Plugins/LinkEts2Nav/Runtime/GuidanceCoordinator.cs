using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.Runtime;

public sealed record GuidancePointDiagnostic(string EventId,string TriggerId,string Text,double RemainingMeters,bool Enabled);
public sealed record GuidanceDiagnostic(int EventCount,int TriggerCount,
    IReadOnlyList<QueuedGuidanceSpeech> Pending,IReadOnlyList<GuidanceTriggerResult> LatestTriggers,string? LastPlayed)
{
    public IReadOnlyList<GuidancePointDiagnostic> Upcoming {get;init;}=[];
}

/// <summary>Single bridge between the event table, trigger scheduler, synthesis cache and playback owner.</summary>
public sealed class GuidanceCoordinator
{
    private readonly SpeechAnnouncementQueue? cache;
    private readonly GuidancePlaybackManager? playback;
    private readonly Action<string> log;
    private readonly object gate=new();
    private string? session,lastPlayed;
    private readonly Dictionary<string,string> playedBySource=new(StringComparer.Ordinal);
    private IReadOnlyList<GuidanceTriggerResult> latest=[];
    private RouteSnapshot? observedRoute;
    private string? replacementRevision;
    private readonly HashSet<string> replacementApplied=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> lastQueued=new(StringComparer.Ordinal);
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
        .OrderBy(p=>p.RemainingMeters).Take(64).ToArray()};}}
    public void Publish(GuidanceSourcePlan plan)
    {
        EnsureSession(plan.SessionId);
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
    public void PublishLandmarks(WarningSnapshot snapshot,IReadOnlyList<PlannedCameraWarning> plans)
    {
        var events=new List<GuidanceEvent>();var triggers=new List<SpeechTrigger>();
        var route=Timeline.Sources.FirstOrDefault(p=>p.Source=="navigation"&&p.Active
            &&p.ObservedAt==snapshot.ObservedAt&&p.CoordinateFrame is not null);
        double playerAlong=route?.PlayerAlongMeters??0;
        foreach(var group in plans.GroupBy(p=>(p.CameraId,p.Kind,p.EncounterRevision)))
        {
            var sample=group.First();string id=$"landmark:{sample.CameraId}:{sample.Kind}:{sample.EncounterRevision}";
            events.Add(new(id,playerAlong+sample.DistanceMeters,"测速照相"){DistanceKind=sample.DistanceKind});
            var stages=group.OrderByDescending(p=>p.StageDistanceMeters).ToArray();
            foreach(var p in stages)
            {
                string trigger=id+":"+p.StageDistanceMeters.ToString(System.Globalization.CultureInfo.InvariantCulture);
                double actual=p.DistanceUntilTriggerMeters>0?p.StageDistanceMeters:p.DistanceMeters;
                triggers.Add(new(trigger,id,SpeechTriggerKind.Advance,playerAlong+(p.DistanceUntilTriggerMeters>0?p.DistanceUntilTriggerMeters:Math.Min(0,p.DistanceMeters-p.StageDistanceMeters)),
                    PlannedSpeechAnnouncer.Text(p.StageDistanceMeters,Math.Min(actual,p.StageDistanceMeters),p.FirstConfirmedInside200Meters,p.SpeedLimitKph,p.Kind))
                    {SequenceOrder=-p.StageDistanceMeters,Supersedes=stages.Where(s=>s.StageDistanceMeters>p.StageDistanceMeters)
                        .Select(s=>id+":"+s.StageDistanceMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()});
            }
        }
        Publish(new("landmarks",snapshot.SessionId,snapshot.RouteRevision,playerAlong,snapshot.IsActive,snapshot.ObservedAt,events,triggers));
    }
    public void Suspend(string source)=>Timeline.Suspend(source);
    public void Observe(GameDataSnapshot data,DateTimeOffset now)
    {
        EnsureSession(data.SessionId);
        if(observedRoute is not null&&observedRoute.Revision!=data.Route.Revision)
        {
            replacementRevision=IsForwardExtension(observedRoute,data.Route)?null:data.Route.Revision;
            replacementApplied.Clear();
        }
        observedRoute=data.Route;
        playback?.SetActive(data.IsVehicleUsable&&data.Vehicle is {Paused:false,SdkActive:true,IsFinite:true,SpeedMetersPerSecond:>=-.1} v
            &&now>=v.ReceivedAt&&now-v.ReceivedAt<=TimeSpan.FromSeconds(2));
    }
    private static bool IsForwardExtension(RouteSnapshot oldRoute,RouteSnapshot newRoute)
    {
        var old=oldRoute.Nodes.Select(n=>n.Uid).ToArray();var current=newRoute.Nodes.Select(n=>n.Uid).ToArray();
        if(old.Length==0||current.Length==0)return old.Length==current.Length;
        for(int offset=0;offset<old.Length;offset++)
        {
            if(old[offset]!=current[0])continue;
            int count=Math.Min(old.Length-offset,current.Length);
            if(Enumerable.Range(0,count).All(i=>old[offset+i]==current[i]))return true;
        }
        return false;
    }
    public void Tick(GameDataSnapshot data,DateTimeOffset now)
    {
        EnsureSession(data.SessionId);
        bool active=data.IsVehicleUsable&&data.Vehicle is {SdkActive:true,Paused:false,IsFinite:true,SpeedMetersPerSecond:>=-.1} v
            &&now>=v.ReceivedAt&&now-v.ReceivedAt<=TimeSpan.FromSeconds(2);
        playback?.SetActive(active);
        if(!active){cache?.ClearPlan("guidance-preparation");latest=[];return;}
        cache?.UpdatePlan("guidance-preparation",Scheduler.Prepare(Timeline,now));
        latest=Scheduler.Update(Timeline,now,data.Vehicle!.SpeedMetersPerSecond);
        foreach(var result in latest)
        {
            if(result.Disposition!=TriggerDisposition.Queued)continue;
            var t=result.Trigger;
            var item=new QueuedGuidanceSpeech(result.Source,result.Event.Id,t.Id,t.SequenceOrder,t.Priority,t.Text);
            lastQueued[result.Source]=t.Text;
            log("语音触发入队："+t.Text+" 事件="+result.Event.Id+" 触发点="+t.Id);
            playback?.Enqueue(item);Queued?.Invoke(item);
        }
    }
    private void EnsureSession(string value)
    {if(session!=value){Reset();session=value;}}
    public void Reset()
    {
        Timeline.Reset();Scheduler.Reset();playback?.Reset();cache?.ClearPlan("guidance-preparation");
        latest=[];lastQueued.Clear();session=null;observedRoute=null;replacementRevision=null;replacementApplied.Clear();lock(gate){lastPlayed=null;playedBySource.Clear();}
    }
}
