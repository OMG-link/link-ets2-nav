namespace LinkEts2Nav.Runtime;

/// <summary>
/// Immutable route identity and SDK progress published together. FullRoute retains the
/// first complete snapshot of this generation; CurrentSnapshot keeps the latest SDK
/// metrics and remaining-route semantics. CurrentNodeIndex indexes FullRoute.Nodes.
/// Generation is the public route version: first available route is 1, every
/// replacement or clear advances it, repeated clears and suffix updates do not.
/// Invalid/stale observations retain structure; Unknown resets it for a new session.
/// Inputs are immutable snapshots produced by RouteSnapshot.Parse.
/// </summary>
public sealed record NavigationRouteState(
    RouteSnapshot FullRoute, RouteSnapshot CurrentSnapshot, int CurrentNodeIndex, long Generation)
{
    public static NavigationRouteState Empty { get; } = new(
        new([], string.Empty, RouteAvailability.Unknown, default),
        new([], string.Empty, RouteAvailability.Unknown, default), 0, 0);

    /// <summary>Only exact UID suffixes advance the pointer. Other valid routes replace it.</summary>
    public NavigationRouteState Accept(RouteSnapshot next)
    {
        if (next.Availability == RouteAvailability.Invalid || next.Availability == RouteAvailability.Stale)
            return this with { CurrentSnapshot = next };
        if (next.Availability is RouteAvailability.None or RouteAvailability.Unknown)
            return new(next, next, 0, Generation + (FullRoute.Nodes.Count == 0 ? 0 : 1));

        int offset = FullRoute.Nodes.Count - next.Nodes.Count;
        bool suffix = FullRoute.Availability == RouteAvailability.Available && offset >= CurrentNodeIndex;
        for (int i = 0; suffix && i < next.Nodes.Count; i++)
            suffix = FullRoute.Nodes[offset + i].Uid == next.Nodes[i].Uid;
        return suffix
            ? this with { CurrentSnapshot = next, CurrentNodeIndex = offset }
            : new(next, next, 0, Generation + 1);
    }
}
