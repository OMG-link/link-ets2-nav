namespace LinkEts2Nav.Core;

/// <summary>A proven lane segment in ordered route coordinates, independent of speech placement.</summary>
public sealed record LaneActionInterval(double StartMeters,double EndMeters)
{
    public double LengthMeters=>EndMeters-StartMeters;
    public double MidpointMeters=>(StartMeters+EndMeters)/2;
}
