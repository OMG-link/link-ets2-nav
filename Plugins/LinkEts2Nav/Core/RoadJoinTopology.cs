namespace LinkEts2Nav.Core;

/// <summary>Native lane reachability facts, separate from how a main entrance is selected.</summary>
public static class RoadJoinTopology
{
    public static bool HasSoleExit(IReadOnlyList<LaneConnection> lanes,ulong entry,ulong exit)
        =>!lanes.Any(c=>c.EntryNode==entry&&c.ExitNode!=exit);

    public static bool PreservesLanes(IReadOnlyList<LaneConnection> group)
        =>group.GroupBy(c=>c.EntryLane).All(g=>g.Select(c=>c.ExitLane).Distinct().Count()==1)
            &&group.Select(c=>c.ExitLane).Distinct().Count()==group.Select(c=>c.EntryLane).Distinct().Count();
}
