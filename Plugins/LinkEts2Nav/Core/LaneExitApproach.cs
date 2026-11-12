using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Moves a dedicated exit-lane decision to its proven upstream lane opening.</summary>
public sealed class LaneExitApproach
{
    private readonly Dictionary<string,(ManeuverKind Kind,string Lanes,double Lead)> leads = new(StringComparer.Ordinal);
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
            if(action.Kind is not (ManeuverKind.KeepRight or ManeuverKind.KeepLeft or ManeuverKind.ChooseLanes)
                ||action.AnnouncementMode!=NavigationAnnouncementMode.Normal||action.JunctionId is null) return action;
            int fork=-1;
            for(int i=0;i<route.Count;i++) if(route[i].Id.StartsWith(action.JunctionId+":",StringComparison.Ordinal)){fork=i;break;}
            if(fork<0||route[fork].Roundabout is not null)return action;
            var alternatives=graph.Where(e=>e.StartNodeUid==route[fork].StartNodeUid&&e.EndNodeUid!=route[fork].EndNodeUid)
                .SelectMany(e=>e.LanePaths).Select(p=>p.EntryOrder).ToHashSet();
            var orders=action.ForkInstruction?.DedicatedOrders??route[fork].LanePaths
                .Where(p=>!alternatives.Contains(p.EntryOrder)).Select(p=>p.EntryOrder).Distinct().Order().ToArray();
            string lanes=string.Join(",",orders);
            var interval=DedicatedLaneOpening.Find(route,graph,offsets,fork,orders);
            if(interval is {} value) leads[action.Id]=(action.Kind,lanes,value.LengthMeters);
            if(!leads.TryGetValue(action.Id,out var remembered)||remembered.Kind!=action.Kind||remembered.Lanes!=lanes)return action;
            double entry=offsets[fork]-vehicleAlong;
            return action with{DistanceMeters=entry-remembered.Lead,EndDistanceMeters=entry};
        }).Where(a=>a.EndDistanceMeters is null or >=0).OrderBy(a=>a.DistanceMeters).ToArray();
    }
    private static double Length(DirectedRoad road)=>road.Points.Zip(road.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
}
