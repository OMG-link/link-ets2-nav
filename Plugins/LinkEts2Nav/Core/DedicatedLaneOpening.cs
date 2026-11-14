namespace LinkEts2Nav.Core;

public sealed record DedicatedLaneProof(LaneActionInterval Interval,ForkSignal? Signal)
{
    public bool? MoveLeft {get;init;}
}

/// <summary>Compatibility query over the same physical index used by production; no route-dependent tracing.</summary>
public static class DedicatedLaneOpening
{
    public static DedicatedLaneProof? Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        double[] offsets,int fork,IReadOnlyList<int> dedicatedOrders,Action<string>? diagnostic=null)
    {
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(graph));
        var edge=route[fork];string junction="prefab:"+NativeLaneGraph.Prefab(edge);
        var action=new NavigationManeuver("proof",ManeuverKind.ChooseLanes,offsets[fork],"in",edge.Id)
            {JunctionId=junction,ForkInstruction=new(ManeuverKind.ChooseLanes,false,null,dedicatedOrders,dedicatedOrders)};
        var selected=AuxiliaryLaneGuidance.Apply([action],route,new([],[],true,null),index).FirstOrDefault(a=>a.Id=="proof");
        foreach(var reason in index.Diagnostics)diagnostic?.Invoke(reason);
        if(selected?.ApproachInterval is not {} interval)return null;
        var corridor=index.Corridors.First(c=>c.Terminals.Any(t=>NativeRoadKey.Of(t.Edge)==NativeRoadKey.Of(edge)));
        var lamp=corridor.StartKind==AuxiliaryBoundaryKind.NativeSplit
            ?corridor.Origin.LanePaths.FirstOrDefault(p=>corridor.OriginSideOrders.Contains(p.ExitOrder))?.NativeBranchSignal:null;
        return new(interval,lamp){MoveLeft=corridor.SideIsLeft};
    }
}
