namespace LinkEts2Nav.Core;

/// <summary>Places acceleration/deceleration actions at ten percent of the proven interval,
/// but never more than 200 metres before its original merge/divergence point (the interval end).</summary>
public static class AuxiliaryLaneTiming
{
    public static double ActionPoint(LaneActionInterval interval)
        =>Math.Max(interval.AtFraction(.1),interval.EndMeters-200);
}
