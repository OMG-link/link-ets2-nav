namespace LinkEts2Nav.Map;

/// <summary>Uniform prefab-chord multiplier; does not query parsed per-instance path lengths.</summary>
public static class JourneyEstimationCalibration
{
    // Eight-region offline routing sample: 4,800 full routes, 295,026 prefab traversals.
    // Total native curve length / total control-interface chord = 1.0278627098 (length-weighted).
    public const double PrefabDistanceFactor=1.028;
}
