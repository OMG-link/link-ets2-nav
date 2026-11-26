namespace LinkEts2Nav.Core;

/// <summary>
/// One roundabout visit on the unchanged route. Visit indices use the original
/// route, the upper index is exclusive, and all action positions are absolute
/// distances from that route's beginning.
/// </summary>
public sealed record RoundaboutRouteRange(string RingId,int StartVisitIndex,int EndVisitIndexExclusive,
    double StartMeters,double EndMeters,IReadOnlyList<NavigationManeuver> Actions)
{
    public bool StartsInside { get; init; }
    public bool EndsInside { get; init; }
    public bool ContainsVisit(int visitIndex)=>visitIndex>=StartVisitIndex&&visitIndex<EndVisitIndexExclusive;
}
