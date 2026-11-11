namespace LinkEts2Nav.Core;

/// <summary>Native lane reachability facts, separate from how a main entrance is selected.</summary>
public static class RoadJoinTopology
{
    public static bool HasSoleExit(IReadOnlyList<LaneConnection> lanes,ulong entry,ulong exit)
        =>!lanes.Any(c=>c.EntryNode==entry&&c.ExitNode!=exit);

    public static bool PreservesLanes(IReadOnlyList<LaneConnection> group)
        =>group.GroupBy(c=>c.EntryLane).All(g=>g.Select(c=>c.ExitLane).Distinct().Count()==1)
            &&group.Select(c=>c.ExitLane).Distinct().Count()==group.Select(c=>c.EntryLane).Distinct().Count();

    /// <summary>Only independent entrance groups whose lanes survive into disjoint exit slots.</summary>
    public static IReadOnlyList<LaneConnection[]> IndependentEntries(IReadOnlyList<LaneConnection>? lanes,ulong exit)
    {
        if(lanes is null)return [];
        var groups=lanes.Where(c=>c.ExitNode==exit).GroupBy(c=>c.EntryNode).Select(g=>g.ToArray()).ToArray();
        if(groups.Length<2||groups.Any(g=>!HasSoleExit(lanes,g[0].EntryNode,exit)||!PreservesLanes(g)))return [];
        if(groups.SelectMany(g=>g.Select(c=>c.ExitLane).Distinct())
            .GroupBy(l=>l).Any(g=>g.Count()>1))return [];
        return groups;
    }
}
