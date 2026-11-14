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
        if(plan.Sequences.Select(s=>s.Id).Distinct(StringComparer.Ordinal).Count()!=plan.Sequences.Count
            ||plan.Events.Select(e=>e.Id).Distinct(StringComparer.Ordinal).Count()!=plan.Events.Count
            ||plan.Triggers.Select(t=>t.Id).Distinct(StringComparer.Ordinal).Count()!=plan.Triggers.Count)
            throw new ArgumentException("Duplicate published series, event or point.",nameof(plan));
        // A complete series survives a moving map window through its end; no
        // separate queue tombstones or loose event/point reconciliation are needed.
        if(plan.CoordinateFrame is not null&&sources.TryGetValue(plan.Source,out var previous)
            &&previous.CoordinateFrame==plan.CoordinateFrame&&plan.PlayerAlongMeters>=previous.PlayerAlongMeters)
        {
            var current=plan.Sequences.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
            var retained=previous.Sequences.Where(s=>!current.Contains(s.Id)
                &&(s.EndAlongMeters is null||s.EndAlongMeters>=previous.PlayerAlongMeters));
            plan=plan with{Sequences=[..plan.Sequences,..retained]};
        }
        sources[plan.Source]=plan;
    }

    public void Suspend(string source)
    {
        if(sources.TryGetValue(source,out var plan))sources[source]=plan with{Active=false,PrepareWhileInactive=false};
    }
    /// <summary>Position ticks refresh progress and freshness without publishing or merging any event structure.</summary>
    public void UpdateProgress(string source,string sessionId,double along,DateTimeOffset observedAt,bool active)
    {
        if(!double.IsFinite(along))throw new ArgumentException("Invalid route progress.",nameof(along));
        if(sources.TryGetValue(source,out var plan)&&plan.SessionId==sessionId)
            sources[source]=plan with{PlayerAlongMeters=along,ObservedAt=observedAt,Active=active,PrepareWhileInactive=active};
    }
    public void Clear(string source)=>sources.Remove(source);
    public void Reset(){sources.Clear();session=null;}
}
