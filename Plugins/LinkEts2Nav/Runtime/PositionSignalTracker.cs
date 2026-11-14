namespace LinkEts2Nav.Runtime;

public sealed record PositionSignalStatus(bool IsWeak,long Episode)
{
    public static readonly PositionSignalStatus None=new(false,0);
}

/// <summary>Counts elapsed unpaused time without a progressing telemetry sample.</summary>
public sealed class PositionSignalTracker
{
    public const string Text="卫星定位信号弱，位置更新可能不及时";
    private DateTimeOffset? observedAt;
    private bool active,paused,weak;
    private TimeSpan elapsed;
    private long episode;

    public void Update(VehicleSnapshot? previous,VehicleSnapshot next)
    {
        Advance(next.ReceivedAt);
        active=next.SdkActive;paused=next.Paused;
        bool progressed=previous is null||next.SimulationTime!=previous.SimulationTime;
        if(!active||next.IsFinite&&progressed)
        {elapsed=TimeSpan.Zero;weak=false;}
    }

    public PositionSignalStatus Capture(DateTimeOffset now)
    {
        Advance(now);
        if(active&&!paused&&!weak&&elapsed>=TimeSpan.FromSeconds(5))
        {weak=true;episode++;}
        return new(active&&weak,episode);
    }

    public void Reset()
    {observedAt=null;active=paused=weak=false;elapsed=TimeSpan.Zero;}

    private void Advance(DateTimeOffset now)
    {
        if(observedAt is {} previous&&now>previous&&active&&!paused)elapsed+=now-previous;
        if(observedAt is null||now>observedAt)observedAt=now;
    }
}
