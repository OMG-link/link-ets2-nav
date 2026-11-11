namespace LinkEts2Nav.Core.Guidance;

/// <summary>One event/trigger table shared by producers. Updated only by the serialized game loop.</summary>
public sealed class GuidanceTimeline
{
    private readonly Dictionary<string,GuidanceSourcePlan> sources=new(StringComparer.Ordinal);
    private string? session;
    public IReadOnlyList<GuidanceSourcePlan> Sources=>sources.Values.OrderBy(p=>p.Source,StringComparer.Ordinal).ToArray();

    public void Publish(GuidanceSourcePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Source);
        if(session!=plan.SessionId){sources.Clear();session=plan.SessionId;}
        if(!double.IsFinite(plan.PlayerAlongMeters))throw new ArgumentException("Invalid route progress.",nameof(plan));
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var e in plan.Events)
            if(string.IsNullOrWhiteSpace(e.Id)||!ids.Add(e.Id)||!double.IsFinite(e.AlongMeters))
                throw new ArgumentException("Invalid or duplicate route event.",nameof(plan));
        var triggerIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var t in plan.Triggers)
            if(!ids.Contains(t.EventId)||!triggerIds.Add(t.Id)||!double.IsFinite(t.AlongMeters))
                throw new ArgumentException("Invalid event trigger.",nameof(plan));
        // Preserve missing events through their known end boundary, including
        // the update crossing it. Event disappearance must not discard the end
        // point needed to withdraw pending speech. No second lifecycle cache is needed.
        if(plan.CoordinateFrame is not null&&sources.TryGetValue(plan.Source,out var previous)
            &&previous.CoordinateFrame==plan.CoordinateFrame&&plan.PlayerAlongMeters>=previous.PlayerAlongMeters)
        {
            var oldSequences=previous.Sequences.ToDictionary(s=>s.Id,StringComparer.Ordinal);
            var crossed=previous.Events.Where(e=>!ids.Contains(e.Id)
                &&(e.AlongMeters>=previous.PlayerAlongMeters&&e.AlongMeters<=plan.PlayerAlongMeters
                    ||oldSequences.TryGetValue(e.SequenceId,out var sequence)
                        &&sequence.EndAlongMeters>=previous.PlayerAlongMeters)).ToArray();
            if(crossed.Length>0)
            {
                var crossedEventIds=crossed.Where(e=>e.AlongMeters>=previous.PlayerAlongMeters
                    &&e.AlongMeters<=plan.PlayerAlongMeters).Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
                var retainedSequences=crossed.Select(e=>e.SequenceId).ToHashSet(StringComparer.Ordinal);
                var currentSequences=plan.Sequences.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
                plan=plan with{Events=plan.Events.Concat(crossed).ToArray(),
                    Triggers=plan.Triggers.Concat(previous.Triggers.Where(t=>crossedEventIds.Contains(t.EventId))).ToArray(),
                    Sequences=plan.Sequences.Concat(previous.Sequences.Where(s=>retainedSequences.Contains(s.Id)
                        &&!currentSequences.Contains(s.Id))).ToArray()};
            }
        }
        sources[plan.Source]=plan with {
            Events=plan.Events.OrderBy(e=>e.AlongMeters).ToArray(),
            Triggers=plan.Triggers.OrderBy(t=>t.AlongMeters).ThenBy(t=>t.Priority).ToArray()};
    }

    public void Suspend(string source)
    {
        if(sources.TryGetValue(source,out var plan))sources[source]=plan with{Active=false,PrepareWhileInactive=false};
    }
    public void Clear(string source)=>sources.Remove(source);
    public void Reset(){sources.Clear();session=null;}
}
