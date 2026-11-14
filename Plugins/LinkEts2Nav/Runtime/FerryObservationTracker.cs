namespace LinkEts2Nav.Runtime;

public sealed record FerryTelemetrySample(bool EventFlag,string SourceId,string TargetId,ulong PaidAmount);
public sealed record FerryUseObservation(long Sequence,string SourceId,string TargetId,DateTimeOffset ObservedAt);
public sealed record VerifiedFerryArrival(string SourceId,string TargetId,long EventSequence,
    string PreviousSessionId,string CurrentSessionId,DateTimeOffset ObservedAt);

/// <summary>The native player.use.ferry handler toggles before writing its payload; confirm two equal reads.</summary>
internal sealed class FerryObservationTracker
{
    private FerryTelemetrySample? previous,pending;
    private DateTimeOffset firstEdge;
    private long sequence,edgeSequence;
    public FerryUseObservation? Latest {get;private set;}
    public void Observe(FerryTelemetrySample sample,DateTimeOffset now)
    {
        if(previous is null){previous=sample;return;} // A latched true flag is not a new event.
        if(previous.EventFlag!=sample.EventFlag){pending=sample;firstEdge=now;edgeSequence=++sequence;}
        else if(pending is not null&&now-firstEdge<=TimeSpan.FromSeconds(1))
        {
            if(pending!=sample)pending=sample;
            else if(!string.IsNullOrWhiteSpace(sample.SourceId)&&!string.IsNullOrWhiteSpace(sample.TargetId)
                &&sample.SourceId!=sample.TargetId)
            {
                Latest=new(edgeSequence,sample.SourceId,sample.TargetId,firstEdge);
            }
        }
        else pending=null;
        // A native edge can expose the previous nonempty payload first; keep this
        // bounded candidate open for the writer to finish, even after two equal reads.
        // Payload changes outside that edge window are never transport evidence.
        previous=sample;
    }
    public void Reset(){previous=pending=null;Latest=null;}
}
