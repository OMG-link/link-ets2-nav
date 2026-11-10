namespace LinkEts2Nav.Runtime;

/// <summary>Legacy replay comparison payload; no longer sampled by the live warning pipeline.</summary>
public sealed record CurrentSpeedLimitDiagnostic(string SessionId,DateTimeOffset SampledAt,
    double X,double Y,double Z,double HeadingTurns,double? ActualKph,int? InferredKph,string Reason)
{
    public bool Failed => InferredKph is null;
    public bool Mismatch => InferredKph is { } inferred && ActualKph is { } actual && Math.Abs(inferred-actual)>0.5;
}
