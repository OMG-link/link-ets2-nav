using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Moves a dedicated exit-lane decision to its proven upstream lane opening.</summary>
public sealed class LaneExitApproach
{
    private readonly Dictionary<string,(ManeuverKind Kind,double Lead)> leads = new(StringComparer.Ordinal);
    public void Reset() => leads.Clear();
    public IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,
        IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,double vehicleAlong)
    {
        double[] offsets=new double[route.Count+1];
        for(int i=0;i<route.Count;i++) offsets[i+1]=offsets[i]+Length(route[i]);
        var active=actions.Select(a=>a.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var key in leads.Keys.Where(k=>!active.Contains(k)).ToArray()) leads.Remove(key);
        return actions.Select(action=>
        {
            if(action.Kind is not (ManeuverKind.KeepRight or ManeuverKind.KeepLeft)
                ||action.AnnouncementMode!=NavigationAnnouncementMode.Normal||action.JunctionId is null) return action;
            int fork=-1;
            for(int i=0;i<route.Count;i++) if(route[i].Id.StartsWith(action.JunctionId+":",StringComparison.Ordinal)){fork=i;break;}
            if(fork<0||route[fork].Roundabout is not null)return action;
            double? lead=DedicatedLaneOpening.Find(route,graph,offsets,fork,action.Kind);
            if(lead is {} value) leads[action.Id]=(action.Kind,value);
            if(!leads.TryGetValue(action.Id,out var remembered)||remembered.Kind!=action.Kind)return action;
            return action with{DistanceMeters=Math.Max(0,action.DistanceMeters-remembered.Lead)};
        }).OrderBy(a=>a.DistanceMeters).ToArray();
    }
    private static double Length(DirectedRoad road)=>road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
}
