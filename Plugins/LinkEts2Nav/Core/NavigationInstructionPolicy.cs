using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Core;

/// <summary>Synchronous adapter over the shared event planner and trigger ledger.</summary>
public sealed class NavigationInstructionPolicy
{
    private readonly NavigationEventPlanner planner=new();
    private readonly GuidanceTimeline timeline=new();
    private readonly SpeechTriggerScheduler scheduler=new();
    // The legacy API supplies remaining distances rather than absolute progress.
    // Shared maneuver identities align those samples to one route coordinate frame.
    private readonly Dictionary<string,double> positions=new(StringComparer.Ordinal);
    private string? session;
    private double playerAlong;
    private DateTimeOffset? observed;

    public IReadOnlyList<string> Plan(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active)
        =>PlanSequence(maneuver is null?[]:[maneuver],sessionId,observedAt,now,active);
    public IReadOnlyList<string> Update(NavigationManeuver? maneuver,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null)
        =>UpdateSequence(maneuver is null?[]:[maneuver],sessionId,observedAt,now,active,canAnnounce);

    public IReadOnlyList<string> PlanSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,bool hasConfirmedContinuation=false,double? initialBoundaryDistanceMeters=null)
    {
        if(!Valid(maneuvers,sessionId,observedAt,now,active))return [];
        timeline.Publish(Build(maneuvers,sessionId,observedAt,hasConfirmedContinuation,initialBoundaryDistanceMeters));
        return scheduler.Prepare(timeline,now);
    }
    public IReadOnlyList<string> UpdateSequence(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,DateTimeOffset now,bool active,Func<string,bool>? canAnnounce=null,
        bool hasConfirmedContinuation=false,double speedMetersPerSecond=0,double? initialBoundaryDistanceMeters=null)
    {
        if(!Valid(maneuvers,sessionId,observedAt,now,active))return [];
        var plan=Build(maneuvers,sessionId,observedAt,hasConfirmedContinuation,initialBoundaryDistanceMeters);
        // A synchronous caller has no playback worker. Keep unavailable points
        // unconsumed; all timing, same-series ordering and ends remain scheduler-owned.
        if(canAnnounce is not null)plan=plan with{Triggers=plan.Triggers.Select(t=>t with
        {
            Enabled=t.Enabled&&(t.Silent||t.SkipAtSpeedKph is {} speed&&speedMetersPerSecond*3.6>=speed||canAnnounce(t.Text))
        }).ToArray()};
        timeline.Publish(plan);observed=observedAt;
        return scheduler.Update(timeline,now,speedMetersPerSecond)
            .Where(r=>r.Disposition==TriggerDisposition.Queued).OrderBy(r=>r.Trigger.Priority)
            .Select(r=>r.Trigger.Text).ToArray();
    }
    private GuidanceSourcePlan Build(IReadOnlyList<NavigationManeuver> maneuvers,string sessionId,
        DateTimeOffset observedAt,bool continuation,double? initialBoundaryDistanceMeters)
    {
        if(session!=sessionId){Reset();session=sessionId;}
        var shared=maneuvers.FirstOrDefault(m=>positions.ContainsKey(m.Id));
        if(shared is not null)playerAlong=positions[shared.Id]-shared.DistanceMeters;
        foreach(var m in maneuvers)positions.TryAdd(m.Id,playerAlong+m.DistanceMeters);
        var plan=planner.Build(sessionId,"standalone",observedAt,playerAlong,maneuvers,true,false,continuation,scheduler,
            initialBoundaryAlong:playerAlong+initialBoundaryDistanceMeters);
        // Read supersession from the common ledger instead of maintaining a
        // second played-history state for this synchronous interface.
        var series=plan.Events.ToDictionary(e=>e.Id,e=>e.SequenceId,StringComparer.Ordinal);
        var announced=plan.Triggers.Where(t=>scheduler.Disposition(plan.Source,t.Id)==TriggerDisposition.Queued)
            .GroupBy(t=>series[t.EventId]).ToDictionary(g=>g.Key,g=>g.Max(t=>t.SequenceOrder),StringComparer.Ordinal);
        return plan with{Triggers=plan.Triggers.Where(t=>!announced.TryGetValue(series[t.EventId],out double rank)||t.SequenceOrder>=rank).ToArray()};
    }
    private bool Valid(IReadOnlyList<NavigationManeuver> list,string sessionId,DateTimeOffset observedAt,
        DateTimeOffset now,bool active)
    {
        if(!active||string.IsNullOrWhiteSpace(sessionId)||now<observedAt||now-observedAt>TimeSpan.FromSeconds(2)
            ||session==sessionId&&observed is {} last&&observedAt<last)return false;
        double previous=-1;var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var m in list)
        {
            if(m is null||string.IsNullOrWhiteSpace(m.Id)||!ids.Add(m.Id)||!Enum.IsDefined(m.Kind)
                ||!double.IsFinite(m.DistanceMeters)||m.DistanceMeters<previous||m.DistanceMeters<0)return false;
            previous=m.DistanceMeters;
        }
        return true;
    }
    public void Reset(){planner.Reset();timeline.Reset();scheduler.Reset();positions.Clear();session=null;playerAlong=0;observed=null;}
}
