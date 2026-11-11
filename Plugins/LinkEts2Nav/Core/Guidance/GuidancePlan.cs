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

/// <summary>A playback series ends strictly after its route boundary, or on an observed passage fact.</summary>
public sealed record SpeechSequence(string Id,double? EndAlongMeters=null,bool Ended=false)
{
    public static string EndTriggerId(string id) => "sequence-end:"+id;
}

public sealed record GuidanceSourcePlan(string Source,string SessionId,string RouteRevision,
    double PlayerAlongMeters,bool Active,DateTimeOffset ObservedAt,
    IReadOnlyList<GuidanceEvent> Events,IReadOnlyList<SpeechTrigger> Triggers)
{
    public IReadOnlyList<SpeechSequence> Sequences { get; init; } = [];
    public string? CoordinateFrame { get; init; }
    public bool PrepareWhileInactive { get; init; }
}

public sealed record GuidanceTriggerResult(string Source,GuidanceEvent Event,
    SpeechTrigger Trigger,TriggerDisposition Disposition);
