namespace LinkEts2Nav.Core.Guidance;

/// <summary>Spacing of fixed distance previews after an arbitrary first briefing.</summary>
public static class DistancePreviewStages
{
    /// <summary>
    /// Assign the first preview to its nearer standard stage. A midpoint belongs
    /// to the farther stage, so the next stage still remains ahead.
    /// Execution cues are not distance previews and must not use this rule.
    /// </summary>
    public static int? FirstStage(double firstDistance,ReadOnlySpan<int> descendingStages)
    {
        if(descendingStages.IsEmpty||firstDistance>descendingStages[0])return null;
        for(int i=1;i<descendingStages.Length;i++)
        {
            int upper=descendingStages[i-1],lower=descendingStages[i];
            if(firstDistance>lower&&firstDistance<=upper)
                return firstDistance>=(upper+lower)/2d?upper:lower;
        }
        return descendingStages[^1];
    }

    public static bool Includes(double firstDistance,int stage,ReadOnlySpan<int> descendingStages)
        =>FirstStage(firstDistance,descendingStages) is not {} first||stage<first;
}
