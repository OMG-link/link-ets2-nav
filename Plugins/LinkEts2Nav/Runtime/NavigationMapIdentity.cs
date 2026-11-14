using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

/// <summary>Snapshot wrappers and camera distances change every scan; navigation map facts do not.</summary>
internal sealed class NavigationMapIdentity(NearbyMapSnapshot snapshot)
{
    public bool Matches(NearbyMapSnapshot value)
        => ReferenceEquals(snapshot.Map,value.Map)
        && ReferenceEquals(snapshot.PrefabPaths,value.PrefabPaths)
        && ReferenceEquals(snapshot.AuxiliaryLanes,value.AuxiliaryLanes)
        && ReferenceEquals(snapshot.SpeedLimits,value.SpeedLimits)
        && ReferenceEquals(snapshot.Ferries,value.Ferries)
        && ReferenceEquals(snapshot.FreeNavigationAreas,value.FreeNavigationAreas)
        && snapshot.RoadStructures.Count==value.RoadStructures.Count
        && snapshot.RoadStructures.All(p=>value.RoadStructures.TryGetValue(p.Key,out var other)&&ReferenceEquals(p.Value,other));
}
