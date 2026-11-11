namespace LinkEts2Nav.Core.Guidance;

public enum SpeechTriggerKind { Overview, Advance, Execution, Passage }
public enum TriggerDisposition { Queued, Silent, SpeedSkipped, Superseded }

/// <summary>A route landmark; camera and maneuver descriptions use the same event model.</summary>
public sealed record GuidanceEvent(string Id,double AlongMeters,string Description)
{
    public WarningDistanceKind DistanceKind { get; init; } = WarningDistanceKind.Road;
}

/// <summary>A concrete location before an event, independently of its cached audio.</summary>
public sealed record SpeechTrigger(string Id,string EventId,SpeechTriggerKind Kind,
    double AlongMeters,string Text)
{
    public int Priority { get; init; } = 10;
    public double SequenceOrder { get; init; }
    public bool Enabled { get; init; } = true;
    public double? SkipAtSpeedKph { get; init; }
    public bool Silent { get; init; }
    public IReadOnlyList<string> Supersedes { get; init; } = [];
}

public sealed record GuidanceSourcePlan(string Source,string SessionId,string RouteRevision,
    double PlayerAlongMeters,bool Active,DateTimeOffset ObservedAt,
    IReadOnlyList<GuidanceEvent> Events,IReadOnlyList<SpeechTrigger> Triggers);

public sealed record GuidanceTriggerResult(string Source,GuidanceEvent Event,
    SpeechTrigger Trigger,TriggerDisposition Disposition);
