namespace LinkEts2Nav.Core.Guidance;

/// <summary>Consumes explicit trigger points; neither discovers events nor constructs speech.</summary>
public sealed class SpeechTriggerScheduler
{
    private readonly Dictionary<(string Source,string Id),(TriggerDisposition Disposition,string EventId)> consumed=[];
    private string? session;
    private static readonly TimeSpan MaximumAge=TimeSpan.FromSeconds(2);
    public bool IsConsumed(string source,string id)=>consumed.ContainsKey((source,id));
    public TriggerDisposition? Disposition(string source,string id)=>consumed.TryGetValue((source,id),out var value)?value.Disposition:null;
    public void ForgetEvent(string source,IEnumerable<string> triggerIds)
    {foreach(var id in triggerIds)consumed.Remove((source,id));}
    public void ForgetEvents(string source,IEnumerable<string> eventIds)
    {
        var ids=eventIds.ToHashSet(StringComparer.Ordinal);
        foreach(var key in consumed.Where(p=>p.Key.Source==source&&ids.Contains(p.Value.EventId)).Select(p=>p.Key).ToArray())
            consumed.Remove(key);
    }
    public void Reset(){consumed.Clear();session=null;}

    /// <summary>Only pending, active plans are prepared. Trigger-time speed never removes audio preparation.</summary>
    public IReadOnlyList<string> Prepare(GuidanceTimeline timeline,DateTimeOffset now)
    {
        EnsureSession(timeline.Sources);
        return timeline.Sources.Where(p=>(p.Active||p.PrepareWhileInactive)&&now>=p.ObservedAt&&now-p.ObservedAt<=MaximumAge).SelectMany(p=>p.Triggers
            .Where(t=>!t.Silent&&!IsConsumed(p.Source,t.Id)&&t.Text.Length>0)
            .Select(t=>(Trigger:t,Distance:t.AlongMeters-p.PlayerAlongMeters)))
            .OrderBy(t=>t.Distance).ThenBy(t=>t.Trigger.Priority)
            .Select(t=>t.Trigger.Text).Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<GuidanceTriggerResult> Update(GuidanceTimeline timeline,DateTimeOffset now,
        double speedMetersPerSecond,string? onlySource=null)
    {
        var sources=timeline.Sources;
        if(sources.Count==0)return [];
        EnsureSession(sources);
        var due=sources.Where(p=>Fresh(p,now)&&(onlySource is null||p.Source==onlySource))
            .SelectMany(p=>p.Triggers.Where(t=>t.Enabled&&t.AlongMeters<=p.PlayerAlongMeters&&!IsConsumed(p.Source,t.Id))
                .Select(t=>(Plan:p,Trigger:t))).OrderBy(p=>p.Trigger.Priority)
            .ThenByDescending(p=>p.Trigger.AlongMeters-p.Plan.PlayerAlongMeters).ToArray();
        var results=new List<GuidanceTriggerResult>();
        foreach(var item in due)
        {
            var p=item.Plan;var t=item.Trigger;
            if(IsConsumed(p.Source,t.Id))continue;
            var e=p.Events.First(e=>e.Id==t.EventId);
            TriggerDisposition disposition;
            if(t.SkipAtSpeedKph is {} threshold&&speedMetersPerSecond*3.6>=threshold)disposition=TriggerDisposition.SpeedSkipped;
            else if(t.Silent||t.Text.Length==0)disposition=TriggerDisposition.Silent;
            else
            {
                disposition=TriggerDisposition.Queued;
            }
            consumed[(p.Source,t.Id)]=(disposition,t.EventId);
            foreach(var id in t.Supersedes)consumed[(p.Source,id)]=(TriggerDisposition.Superseded,t.EventId);
            results.Add(new(p.Source,e,t,disposition));
        }
        return results;
    }
    private void EnsureSession(IReadOnlyList<GuidanceSourcePlan> sources)
    {
        if(sources.Count>0&&session!=sources[0].SessionId){consumed.Clear();session=sources[0].SessionId;}
    }
    private static bool Fresh(GuidanceSourcePlan plan,DateTimeOffset now)
        =>plan.Active&&now>=plan.ObservedAt&&now-plan.ObservedAt<=MaximumAge;
}
