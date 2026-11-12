using System.Numerics;

namespace LinkEts2Nav.Core.Guidance;

public enum SpeechTriggerKind { Overview, Advance, Execution, Passage, SequenceEnd }
public enum TriggerDisposition { Queued, Silent, SpeedSkipped, Superseded, Ended }

/// <summary>A route landmark; camera and maneuver descriptions use the same event model.</summary>
public sealed record GuidanceEvent(string Id,double AlongMeters,string Description)
{
    public string? SpeechSequenceId { get; init; }
    public string SequenceId => SpeechSequenceId ?? Id;
    public WarningDistanceKind DistanceKind { get; init; } = WarningDistanceKind.Road;
}

/// <summary>A concrete location before an event, independently of its cached audio.</summary>
public sealed record SpeechTrigger(string Id,string EventId,SpeechTriggerKind Kind,
    double AlongMeters,string Text)
{
    public int Priority { get; init; } = 10;
    public double SequenceOrder { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Points placed at a preceding action's end become due after crossing that boundary.</summary>
    public bool StrictCrossing { get; init; }
    public double? SkipAtSpeedKph { get; init; }
    public bool Silent { get; init; }
    public IReadOnlyList<string> Supersedes { get; init; } = [];
}

public abstract record SpeechEndBoundary;
public sealed record RouteSpeechEnd(double AlongMeters):SpeechEndBoundary;
/// <summary>Existing camera passage detection observes this physical boundary; it is not a guessed route distance.</summary>
public sealed record CameraSpeechEnd(Vector3 Position,Vector3? TravelDirection=null,
    double NearMeters=20,double LeaveMeters=50):SpeechEndBoundary;

/// <summary>A complete, immutable playback series: ordered points and one mandatory physical end.</summary>
public sealed class SpeechSequence
{
    public string Id { get; }
    public SpeechEndBoundary End { get; }
    public double? EndAlongMeters => (End as RouteSpeechEnd)?.AlongMeters;
    public bool Ended { get; }
    public IReadOnlyList<GuidanceEvent> Events { get; }
    public IReadOnlyList<SpeechTrigger> Triggers { get; }
    public SpeechSequence(string id,SpeechEndBoundary end,IReadOnlyList<GuidanceEvent> events,
        IReadOnlyList<SpeechTrigger> triggers,bool ended=false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        bool finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
        if(end is not RouteSpeechEnd {AlongMeters:var along} ||!double.IsFinite(along))
        {
            if(end is not CameraSpeechEnd camera||!finite(camera.Position)
                ||camera.TravelDirection is {} direction&&(!finite(direction)||direction.LengthSquared()<=0)
                ||!double.IsFinite(camera.NearMeters)||!double.IsFinite(camera.LeaveMeters)
                ||camera.NearMeters<=0||camera.LeaveMeters<=camera.NearMeters)
                throw new ArgumentException("A series needs a finite route end or a real camera passage boundary.",nameof(end));
        }
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var e in events)
            if(string.IsNullOrWhiteSpace(e.Id)||!ids.Add(e.Id)||e.SequenceId!=id||!double.IsFinite(e.AlongMeters))
                throw new ArgumentException("Invalid, duplicate or unbound series event.",nameof(events));
        if(ids.Count==0)throw new ArgumentException("A series needs an owning event.",nameof(events));
        var points=new HashSet<string>(StringComparer.Ordinal);
        foreach(var t in triggers)
            if(!ids.Contains(t.EventId)||string.IsNullOrWhiteSpace(t.Id)||!points.Add(t.Id)
                ||t.Kind==SpeechTriggerKind.SequenceEnd||!double.IsFinite(t.AlongMeters)||!double.IsFinite(t.SequenceOrder)
                ||end is RouteSpeechEnd routeEnd&&t.AlongMeters>routeEnd.AlongMeters
                ||t.SkipAtSpeedKph is {} speed&&(!double.IsFinite(speed)||speed<0))
                throw new ArgumentException("Invalid or unbound series point.",nameof(triggers));
        if(ended&&triggers.Count>0)throw new ArgumentException("An ended series cannot introduce speech points.",nameof(triggers));
        var ordered=triggers.OrderBy(t=>t.AlongMeters).ThenBy(t=>t.SequenceOrder).ToArray();
        for(int i=1;i<ordered.Length;i++)
            if(ordered[i].SequenceOrder<=ordered[i-1].SequenceOrder)
                throw new ArgumentException("Series points need stable, strictly increasing order along the route.",nameof(triggers));
        Id=id;End=end;Ended=ended;
        Events=Array.AsReadOnly(events.OrderBy(e=>e.AlongMeters).ToArray());
        Triggers=Array.AsReadOnly(ordered
            .Select(t=>t with{Supersedes=Array.AsReadOnly(t.Supersedes.ToArray())}).ToArray());
    }
    public SpeechSequence MapTriggers(Func<SpeechTrigger,SpeechTrigger> transform)
        =>new(Id,End,Events,Triggers.Select(transform).ToArray(),Ended);
    public SpeechSequence FilterTriggers(Func<SpeechTrigger,bool> predicate)
        =>new(Id,End,Events,Triggers.Where(predicate).ToArray(),Ended);
    public static string EndTriggerId(string id) => "sequence-end:"+id;
}

public sealed record GuidanceSourcePlan(string Source,string SessionId,string RouteRevision,
    double PlayerAlongMeters,bool Active,DateTimeOffset ObservedAt,IReadOnlyList<SpeechSequence> Sequences)
{
    private IReadOnlyList<SpeechSequence> sequences=Array.AsReadOnly(Sequences.ToArray());
    public IReadOnlyList<SpeechSequence> Sequences {get=>sequences;init=>sequences=Array.AsReadOnly(value.ToArray());}
    public IReadOnlyList<GuidanceEvent> Events=>Sequences.SelectMany(s=>s.Events).ToArray();
    public IReadOnlyList<SpeechTrigger> Triggers=>Sequences.SelectMany(s=>s.Triggers).ToArray();
    public string? CoordinateFrame { get; init; }
    public bool PrepareWhileInactive { get; init; }
}

/// <summary>Only the scheduler can create playback requests, from a complete published series.</summary>
public sealed class GuidanceTriggerResult
{
    public string Source {get;}
    public SpeechSequence Sequence {get;}
    public GuidanceEvent Event {get;}
    public SpeechTrigger Trigger {get;}
    public TriggerDisposition Disposition {get;}
    internal GuidanceTriggerResult(string source,SpeechSequence sequence,GuidanceEvent @event,
        SpeechTrigger trigger,TriggerDisposition disposition)
    {Source=source;Sequence=sequence;Event=@event;Trigger=trigger;Disposition=disposition;}
    internal GuidanceTriggerResult WithDisposition(TriggerDisposition disposition)=>new(Source,Sequence,Event,Trigger,disposition);
}
