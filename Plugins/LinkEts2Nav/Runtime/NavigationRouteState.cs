namespace LinkEts2Nav.Runtime;

/// <summary>FullRoute and CurrentNodeIndex address one geometry window. CurrentSnapshot is the raw SDK sample.</summary>
public sealed record NavigationRouteState(
    RouteSnapshot FullRoute, RouteSnapshot CurrentSnapshot, int CurrentNodeIndex, long Generation)
{
    public static NavigationRouteState Empty { get; } = new(
        new([], string.Empty, RouteAvailability.Unknown, default),
        new([], string.Empty, RouteAvailability.Unknown, default), 0, 0);

    /// <summary>Changes when physical geometry or its local visit indices must be rebuilt.</summary>
    public long GeometryGeneration => Generation;
    /// <summary>Changes on a fresh route, proven replacement, or confirmed clear. Roll-forward windows retain it.</summary>
    public long ContinuityGeneration { get; private init; }
    /// <summary>Visit ordinal of FullRoute.Nodes[0] in the current continuous route.</summary>
    internal long FirstNodeOrdinal { get; private init; }
    internal RouteAvailability Availability { get; private init; } = RouteAvailability.Unknown;
    private RouteSnapshot? lastAvailable;
    private ulong? confirmedGoal;
    // A shortened SDK window can hide an already proved branch. Keep at most one older
    // window until the current view covers its remaining tail or chooses another path.
    private RouteSnapshot? forwardWindow;
    private long forwardWindowOrdinal;
    private object? source;
    internal bool HasSameSource(NavigationRouteState other) => source is not null && ReferenceEquals(source, other.source);
    internal bool MatchesAvailableRevision(string revision) =>
        Availability == RouteAvailability.Available && CurrentSnapshot.Revision == revision;

    public NavigationRouteState Accept(RouteSnapshot next) => Accept(next, next.Availability);

    internal NavigationRouteState Clear(RouteSnapshot raw)
    {
        var empty = raw.Nodes.Count == 0 ? raw : new RouteSnapshot([], string.Empty, RouteAvailability.None, raw.ReceivedAt);
        int increment = lastAvailable is null ? 0 : 1;
        return new(empty, raw, 0, GeometryGeneration + increment)
        { source = source, ContinuityGeneration = ContinuityGeneration + increment, Availability = raw.Availability == RouteAvailability.Unknown
            ? RouteAvailability.Unknown : RouteAvailability.None };
    }

    internal NavigationRouteState Accept(RouteSnapshot next, RouteAvailability availability)
    {
        if (ReferenceEquals(CurrentSnapshot, next) && Availability == availability) return this;
        if (availability == RouteAvailability.None) return Clear(next);
        if (availability != RouteAvailability.Available)
            return this with { CurrentSnapshot = next, Availability = availability };

        var goal = RouteContinuity.ConfirmedGoal(next);
        long head = FirstNodeOrdinal + CurrentNodeIndex;
        var reference = forwardWindow ?? lastAvailable;
        long referenceStart = forwardWindow is null ? head : forwardWindowOrdinal;
        // Even a single node can be the exact suffix of the currently visible window.
        // A lone node shared with a different future window cannot establish direction.
        int visibleSuffix = forwardWindow is not null && lastAvailable is not null
            ? RouteContinuity.ExactSuffixOffset(lastAvailable, next) : -1;
        var change = visibleSuffix >= 0 ? new RouteChange(visibleSuffix == 0 ? RouteChangeKind.Same : RouteChangeKind.PrefixTrimmed, visibleSuffix)
            : reference is null || (forwardWindow is not null && next.Nodes.Count == 1) ? new RouteChange(RouteChangeKind.Replaced)
            : RouteContinuity.Compare(reference, next, checked((int)(head - referenceStart)));
        bool replaced = change.Kind == RouteChangeKind.Replaced || (confirmedGoal is {} oldGoal && goal is {} newGoal && oldGoal != newGoal);
        long nextHead = replaced ? 0 : head + change.PreviousOffset;
        long nextEnd = nextHead + next.Nodes.Count;
        bool sameGeometry = !replaced && nextEnd == FirstNodeOrdinal + FullRoute.Nodes.Count
            && FullRoute.MayBeTruncated == next.MayBeTruncated && RouteContinuity.ConfirmedGoal(FullRoute) == goal;
        return new NavigationRouteState(sameGeometry ? FullRoute : next, next, sameGeometry ? checked((int)(nextHead - FirstNodeOrdinal)) : 0,
            GeometryGeneration + (sameGeometry ? 0 : 1))
        {
            ContinuityGeneration = ContinuityGeneration + (replaced ? 1 : 0),
            FirstNodeOrdinal = sameGeometry ? FirstNodeOrdinal : nextHead,
            Availability = availability,
            lastAvailable = next,
            source = source ?? new object(),
            confirmedGoal = goal ?? (replaced ? null : confirmedGoal),
            forwardWindow = !replaced && reference is not null && referenceStart + reference.Nodes.Count > nextEnd ? reference : null,
            forwardWindowOrdinal = referenceStart
        };
    }
}

/// <summary>Live frames already carry their ingress facts. Direct callers can supply raw frames in sequence.</summary>
internal sealed class NavigationRouteCursor
{
    private string? session;
    internal NavigationRouteState Current { get; private set; } = NavigationRouteState.Empty;

    internal NavigationRouteState Observe(GameDataSnapshot data)
    {
        if (session != data.SessionId) { Reset(); session = data.SessionId; }
        var supplied = data.NavigationRoute;
        if (ReferenceEquals(supplied.CurrentSnapshot, data.Route) && supplied.Availability == data.RouteAvailability
            && (Current.GeometryGeneration == 0 || Current.HasSameSource(supplied)))
            return Current = supplied;
        return Current = Current.Accept(data.Route, data.RouteAvailability);
    }

    internal void ResumeAfterFerry(string newSession) => session = newSession;
    internal void Reset() { Current = NavigationRouteState.Empty; session = null; }
}
