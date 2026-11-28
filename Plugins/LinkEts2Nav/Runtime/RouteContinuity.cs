namespace LinkEts2Nav.Runtime;

internal enum RouteChangeKind { Same, PrefixTrimmed, TailExtended, TailShortened, Replaced }
internal readonly record struct RouteChange(RouteChangeKind Kind, int PreviousOffset = 0);

/// <summary>Compares the ordered SDK visits. Repeated physical nodes are not interchangeable visits.</summary>
public static class RouteContinuity
{
    internal static int ExactSuffixOffset(RouteSnapshot previous, RouteSnapshot next)
    {
        int offset = previous.Nodes.Count - next.Nodes.Count;
        if (offset < 0 || next.Nodes.Count == 0) return -1;
        if (offset == 0 && previous.Revision == next.Revision) return 0;
        for (int i = 0; i < next.Nodes.Count; i++)
            if (previous.Nodes[offset + i].Uid != next.Nodes[i].Uid) return -1;
        return offset;
    }

    internal static RouteChange Compare(RouteSnapshot previous, RouteSnapshot next, int first = 0)
    {
        int suffix = ExactSuffixOffset(previous, next);
        if (suffix >= first) return new(suffix == first ? RouteChangeKind.Same : RouteChangeKind.PrefixTrimmed, suffix - first);
        int oldCount = previous.Nodes.Count - first, newCount = next.Nodes.Count;
        if (oldCount < 2 || newCount < 2) return new(RouteChangeKind.Replaced);

        // Z lengths in "new, separator, old" give the overlap at every previous visit in
        // linear time, including a long loop with thousands of repeated node IDs.
        int start = newCount + 1;
        var matches = new int[start + oldCount];
        ulong Uid(int i) => i < newCount ? next.Nodes[i].Uid : i == newCount ? 0 : previous.Nodes[first + i - start].Uid;
        int left = 0, right = 0, offset = -1;
        for (int i = 1; i < matches.Length; i++)
        {
            int length = i < right ? Math.Min(right - i, matches[i - left]) : 0;
            while (i + length < matches.Length && Uid(length) == Uid(i + length)) length++;
            matches[i] = length;
            if (i + length > right) { left = i; right = i + length; }
            if (i < start || length < 2 || length != Math.Min(newCount, matches.Length - i)) continue;
            if (offset >= 0) return new(RouteChangeKind.Replaced);
            offset = i - start;
        }
        return offset < 0 ? new(RouteChangeKind.Replaced)
            : new(newCount > oldCount - offset ? RouteChangeKind.TailExtended : RouteChangeKind.TailShortened, offset);
    }

    public static bool IsForwardExtension(RouteSnapshot oldRoute, RouteSnapshot newRoute) =>
        oldRoute.Availability == RouteAvailability.Available && newRoute.Availability == RouteAvailability.Available
        && !(oldRoute.ConfirmedGoalUid is {} oldGoal && newRoute.ConfirmedGoalUid is {} goal && oldGoal != goal)
        && Compare(oldRoute, newRoute).Kind != RouteChangeKind.Replaced;
}
