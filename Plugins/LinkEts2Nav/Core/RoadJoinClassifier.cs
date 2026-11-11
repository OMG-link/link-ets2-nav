namespace LinkEts2Nav.Core;

public enum RoadJoinRole { None, Continuing, Entering, Ambiguous }
public sealed record RoadJoinDecision(RoadJoinRole Role, ulong? MainEntry = null);

/// <summary>Lane reachability and preservation establish roles; curve angles never do.</summary>
public static class RoadJoinClassifier
{
    public static RoadJoinDecision Classify(IReadOnlyList<LaneConnection>? lanes, ulong entry, ulong exit)
    {
        if(lanes is null) return new(RoadJoinRole.Ambiguous);
        var current=lanes.Where(c=>c.EntryNode==entry && c.ExitNode==exit).ToArray();
        if(current.Length==0) return new(RoadJoinRole.Ambiguous);
        var other=lanes.Where(c=>c.EntryNode!=entry && c.ExitNode==exit)
            .GroupBy(c=>c.EntryNode).Select(g=>g.ToArray()).ToArray();
        if(other.Length==0) return new(RoadJoinRole.None);
        if(lanes.Any(c=>c.EntryNode==entry && c.ExitNode!=exit)) return new(RoadJoinRole.Ambiguous);
        bool Branch(LaneConnection[] branch,LaneConnection[] main)
        {
            if(!RoadJoinTopology.HasSoleExit(lanes,branch[0].EntryNode,exit)
                ||!RoadJoinTopology.HasSoleExit(lanes,main[0].EntryNode,exit)||!RoadJoinTopology.PreservesLanes(main))return false;
            int inputs=branch.Select(c=>c.EntryLane).Distinct().Count();
            var outputs=branch.Select(c=>c.ExitLane).Distinct().ToArray();
            int mainInputs=main.Select(c=>c.EntryLane).Distinct().Count();
            bool contraction=outputs.Length<inputs && mainInputs>outputs.Length && mainInputs>=inputs;
            bool sharedLane=inputs<mainInputs && outputs.Intersect(main.Select(c=>c.ExitLane)).Any();
            return contraction||sharedLane;
        }
        var mains=other.Where(g=>Branch(current,g)).ToArray();
        if(mains.Length==1)return new(RoadJoinRole.Entering,mains[0][0].EntryNode);
        if(mains.Length>1)return new(RoadJoinRole.Ambiguous);
        if(other.Any(g=>Branch(g,current)))return new(RoadJoinRole.Continuing,entry);
        return new(RoadJoinRole.Ambiguous);
    }
}
