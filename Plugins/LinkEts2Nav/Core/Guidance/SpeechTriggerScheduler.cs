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
        return timeline.Sources.Where(p=>(p.Active||p.PrepareWhileInactive)&&now>=p.ObservedAt&&now-p.ObservedAt<=MaximumAge)
            .SelectMany(p=>p.Sequences.Where(s=>!IsConsumed(p.Source,SpeechSequence.EndTriggerId(s.Id))).SelectMany(s=>s.Triggers)
            .Where(t=>!t.Silent&&!IsConsumed(p.Source,t.Id)&&t.Text.Length>0)
            .Select(t=>(Trigger:t,Distance:(t.Window?.StartAlongMeters??t.AlongMeters)-p.PlayerAlongMeters)))
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
                &&!IsConsumed(p.Source,point.Trigger.Id)
                &&Due(p,point.Trigger,point.Observed,now,speedMetersPerSecond)).Select(point=>(Plan:p,Trigger:point.Trigger)))
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
            if(t.Window is not null&&disposition==TriggerDisposition.Queued)
            {
                // A conditional final cue can precede an earlier-order fixed
                // point at high speed. Retire that series prefix immediately.
                foreach(var older in p.Sequences.First(s=>s.Id==e.SequenceId).Triggers.Where(x=>x.SequenceOrder<t.SequenceOrder))
                    if(!IsConsumed(p.Source,older.Id))consumed[(p.Source,older.Id)]=(TriggerDisposition.Superseded,older.EventId);
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
                        results[prior]=old.WithDisposition(TriggerDisposition.Superseded);
                        consumed[(old.Source,old.Trigger.Id)]=(TriggerDisposition.Superseded,old.Event.Id);
                    }
                    else disposition=TriggerDisposition.Superseded;
                }
                if(disposition==TriggerDisposition.Queued)pending[key]=results.Count;
                else if(disposition==TriggerDisposition.Ended)pending.Remove(key);
            }
            consumed[(p.Source,t.Id)]=(disposition,t.EventId);
            results.Add(new(p.Source,p.Sequences.First(s=>s.Id==e.SequenceId),e,t,disposition));
        }
        return results;
    }
    private static bool Due(GuidanceSourcePlan plan,SpeechTrigger trigger,bool observed,DateTimeOffset now,double speed)
    {
        if(observed)return true;
        bool deadlinePassed=trigger.Kind==SpeechTriggerKind.SequenceEnd||trigger.StrictCrossing
            ?trigger.AlongMeters<plan.PlayerAlongMeters:trigger.AlongMeters<=plan.PlayerAlongMeters;
        if(deadlinePassed)return true;
        if(trigger.Window is not {} window||plan.PlayerAlongMeters<window.StartAlongMeters
            ||window.StrictStart&&plan.PlayerAlongMeters==window.StartAlongMeters)return false;
        var owner=plan.Events.First(e=>e.Id==trigger.EventId);
        return window.ShouldTrigger(new(owner,trigger,plan.PlayerAlongMeters,speed,now));
    }

    private IEnumerable<(SpeechTrigger Trigger,bool Observed)> Points(GuidanceSourcePlan plan)
    {
        foreach(var sequence in plan.Sequences)
        {
            if(IsConsumed(plan.Source,SpeechSequence.EndTriggerId(sequence.Id)))continue;
            foreach(var trigger in sequence.Triggers)yield return (trigger,false);
            var owner=plan.Events.LastOrDefault(e=>e.SequenceId==sequence.Id);
            if(!sequence.Ended&&sequence.EndAlongMeters is null)continue;
            // An observed passage fact is already beyond its boundary, even when
            // no absolute along coordinate is available (e.g. no-navigation cameras).
            double end=sequence.Ended?plan.PlayerAlongMeters:sequence.EndAlongMeters!.Value;
            yield return (new SpeechTrigger(SpeechSequence.EndTriggerId(sequence.Id),owner!.Id,SpeechTriggerKind.SequenceEnd,end,"")
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
