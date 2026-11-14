namespace LinkEts2Nav.Core;

/// <summary>Compatibility adapter; all recognition is owned by the immutable native auxiliary index.</summary>
public sealed class LaneExitApproach
{
    private IReadOnlyList<DirectedRoad>? graph;
    private AuxiliaryLaneIndex? index;
    public IReadOnlyList<RoadForkDiagnostic> Diagnostics {get;private set;}=[];
    public void Reset(){graph=null;index=null;Diagnostics=[];}
    public IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,
        IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> native,double vehicleAlong)
    {
        if(!ReferenceEquals(graph,native)){graph=native;index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(native));}
        Diagnostics=index!.Diagnostics.Select(d=>new RoadForkDiagnostic("auxiliary",0,0,d)).ToArray();
        var absolute=actions.Select(a=>a with{DistanceMeters=a.DistanceMeters+vehicleAlong,EndDistanceMeters=a.EndDistanceMeters+vehicleAlong}).ToArray();
        return AuxiliaryLaneGuidance.Apply(absolute,route,new([],[],true,null),index)
            .Where(a=>actions.Any(original=>original.Id==a.Id))
            .Where(a=>a.EndDistanceMeters is null||a.EndDistanceMeters>=vehicleAlong)
            .Select(a=>a with{DistanceMeters=a.DistanceMeters-vehicleAlong,EndDistanceMeters=a.EndDistanceMeters-vehicleAlong,
                ApproachInterval=a.ApproachInterval is {} i?new(i.StartMeters-vehicleAlong,i.EndMeters-vehicleAlong):null}).ToArray();
    }
}
