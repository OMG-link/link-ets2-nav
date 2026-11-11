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
            .SelectMany(p=>Points(p).Where(point=>point.Trigger.Enabled
                &&(point.Observed||(point.Trigger.Kind==SpeechTriggerKind.SequenceEnd||point.Trigger.StrictCrossing?point.Trigger.AlongMeters<p.PlayerAlongMeters:point.Trigger.AlongMeters<=p.PlayerAlongMeters))
                &&!IsConsumed(p.Source,point.Trigger.Id)).Select(point=>(Plan:p,Trigger:point.Trigger)))
            .OrderBy(p=>p.Trigger.AlongMeters-p.Plan.PlayerAlongMeters)
            .ThenBy(p=>p.Trigger.Kind==SpeechTriggerKind.SequenceEnd?1:0)
            .ThenBy(p=>p.Trigger.SequenceOrder).ToArray();
        var results=new List<GuidanceTriggerResult>();
        var pending=new Dictionary<(string Source,string Sequence),int>();
        foreach(var item in due)
        {
            var p=item.Plan;var t=item.Trigger;
            if(IsConsumed(p.Source,t.Id))continue;
            var e=p.Events.First(e=>e.Id==t.EventId);
            TriggerDisposition disposition;
            if(t.Kind==SpeechTriggerKind.SequenceEnd)disposition=TriggerDisposition.Ended;
            else if(t.SkipAtSpeedKph is {} threshold&&speedMetersPerSecond*3.6>=threshold)disposition=TriggerDisposition.SpeedSkipped;
            else if(t.Silent||t.Text.Length==0)disposition=TriggerDisposition.Silent;
            else
            {
                disposition=TriggerDisposition.Queued;
            }
            consumed[(p.Source,t.Id)]=(disposition,t.EventId);
            foreach(var id in t.Supersedes)consumed[(p.Source,id)]=(TriggerDisposition.Superseded,t.EventId);
            var key=(p.Source,e.SequenceId);
            if(disposition is TriggerDisposition.Queued or TriggerDisposition.Ended)
            {
                if(pending.TryGetValue(key,out int prior))
                {
                    var old=results[prior];
                    if(disposition==TriggerDisposition.Ended||t.SequenceOrder>=old.Trigger.SequenceOrder)
                    {
                        results[prior]=old with{Disposition=TriggerDisposition.Superseded};
                        consumed[(old.Source,old.Trigger.Id)]=(TriggerDisposition.Superseded,old.Event.Id);
                    }
                    else disposition=TriggerDisposition.Superseded;
                }
                if(disposition==TriggerDisposition.Queued)pending[key]=results.Count;
                else if(disposition==TriggerDisposition.Ended)pending.Remove(key);
            }
            consumed[(p.Source,t.Id)]=(disposition,t.EventId);
            results.Add(new(p.Source,e,t,disposition));
        }
        return results;
    }
    private static IEnumerable<(SpeechTrigger Trigger,bool Observed)> Points(GuidanceSourcePlan plan)
    {
        foreach(var trigger in plan.Triggers)yield return (trigger,false);
        foreach(var sequence in plan.Sequences)
        {
            var owner=plan.Events.LastOrDefault(e=>e.SequenceId==sequence.Id);
            if(owner is null||!sequence.Ended&&sequence.EndAlongMeters is null)continue;
            // An observed passage fact is already beyond its boundary, even when
            // no absolute along coordinate is available (e.g. no-navigation cameras).
            double end=sequence.Ended?plan.PlayerAlongMeters:sequence.EndAlongMeters!.Value;
            yield return (new SpeechTrigger(SpeechSequence.EndTriggerId(sequence.Id),owner.Id,SpeechTriggerKind.SequenceEnd,end,"")
                {SequenceOrder=0},sequence.Ended);
        }
    }
    private void EnsureSession(IReadOnlyList<GuidanceSourcePlan> sources)
    {
        if(sources.Count>0&&session!=sources[0].SessionId){consumed.Clear();session=sources[0].SessionId;}
    }
    private static bool Fresh(GuidanceSourcePlan plan,DateTimeOffset now)
        =>plan.Active&&now>=plan.ObservedAt&&now-plan.ObservedAt<=MaximumAge;
}
