namespace LinkEts2Nav.Core;

/// <summary>Compatibility adapter for callers passing a raw graph. Production consumes the snapshot index directly.</summary>
public sealed class AccelerationLaneApproach
{
    private IReadOnlyList<DirectedRoad>? graph;
    private AuxiliaryLaneIndex? index;
    public void Reset(){graph=null;index=null;}
    public IReadOnlyList<NavigationManeuver> Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> native,double vehicleAlong)
    {
        if(!ReferenceEquals(graph,native)){graph=native;index=AuxiliaryLaneIndex.Build(new NativeLaneGraph(native));}
        return AuxiliaryLaneGuidance.Apply([],route,new([],[],true,null),index!)
            .Where(a=>a.Kind is ManeuverKind.MergeLeft or ManeuverKind.MergeRight&&a.EndDistanceMeters>=vehicleAlong)
            .Select(a=>a with{DistanceMeters=a.DistanceMeters-vehicleAlong,EndDistanceMeters=a.EndDistanceMeters-vehicleAlong,
                ApproachInterval=a.ApproachInterval is {} i?new(i.StartMeters-vehicleAlong,i.EndMeters-vehicleAlong):null}).ToArray();
    }
}
