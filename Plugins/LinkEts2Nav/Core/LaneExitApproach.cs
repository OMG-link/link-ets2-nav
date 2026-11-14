using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>Moves a dedicated exit-lane decision to its proven upstream lane opening.</summary>
public sealed class LaneExitApproach
{
    private sealed record Adjustment(ManeuverKind Original,string Lanes,double Lead,ForkSignal? Signal,bool? MoveLeft,bool MainContinuation);
    private readonly Dictionary<string,Adjustment> leads = new(StringComparer.Ordinal);
    private readonly List<RoadForkDiagnostic> diagnostics=[];
    public IReadOnlyList<RoadForkDiagnostic> Diagnostics=>diagnostics;
    public void Reset(){leads.Clear();diagnostics.Clear();}
    public IReadOnlyList<NavigationManeuver> Apply(IReadOnlyList<NavigationManeuver> actions,
        IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,double vehicleAlong)
    {
        diagnostics.Clear();
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
            var alternatives=graph.Where(e=>e.Id.StartsWith(action.JunctionId+":",StringComparison.Ordinal)
                &&e.StartNodeUid==route[fork].StartNodeUid&&e.EndNodeUid!=route[fork].EndNodeUid)
                .SelectMany(e=>e.LanePaths).Select(p=>p.EntryOrder).ToHashSet();
            var orders=action.ForkInstruction?.DedicatedOrders??route[fork].LanePaths
                .Where(p=>!alternatives.Contains(p.EntryOrder)).Select(p=>p.EntryOrder).Distinct().Order().ToArray();
            string lanes=string.Join(",",orders);
            var proof=DedicatedLaneOpening.Find(route,graph,offsets,fork,orders,
                reason=>diagnostics.Add(new(action.JunctionId,route[fork].StartNodeUid,route[fork].EndNodeUid,reason)));
            if(proof is {} value) leads[action.Id]=new(action.Kind,lanes,value.Interval.LengthMeters,value.Signal,value.MoveLeft,false);
            else if(route[fork].ForkInfo is {} info)
            {
                var choices=graph.Where(e=>e.Id.StartsWith(action.JunctionId+":",StringComparison.Ordinal)
                    &&e.StartNodeUid==route[fork].StartNodeUid&&e.ForkInfo is not null).ToArray();
                var auxiliary=new HashSet<ulong>();
                foreach(var choice in choices)
                {
                    var instruction=RoadForkRules.Decide(info,choice.EndNodeUid);
                    if(DedicatedLaneOpening.Find([..route.Take(fork),choice],graph,offsets,fork,instruction.DedicatedOrders) is not null)
                        auxiliary.Add(choice.EndNodeUid);
                }
                var mains=info.Branches.Where(b=>!auxiliary.Contains(b.ExitNode)).ToArray();
                if(auxiliary.Count>0&&mains.Length==1&&mains[0].ExitNode==route[fork].EndNodeUid&&mains[0].Signal==ForkSignal.None)
                    leads[action.Id]=new(action.Kind,lanes,0,null,null,true);
            }
            if(!leads.TryGetValue(action.Id,out var remembered)||remembered.Original!=action.Kind||remembered.Lanes!=lanes)return action;
            if(remembered.MainContinuation)return action with{Kind=ManeuverKind.Straight,AnnouncementMode=NavigationAnnouncementMode.Silent,
                IsMainRoadContinuation=true,Instruction=null,NearInstruction=null,
                ForkInstruction=action.ForkInstruction is {} f?f with{Kind=ManeuverKind.Straight,Silent=true,Text=null}:null};
            double entry=offsets[fork]-vehicleAlong;
            var kind=remembered.MoveLeft is {} moveLeft?(moveLeft?ManeuverKind.KeepLeft:ManeuverKind.KeepRight):
                remembered.Signal switch{ForkSignal.Left=>ManeuverKind.KeepLeft,ForkSignal.Right=>ManeuverKind.KeepRight,_=>action.Kind};
            return action with{Kind=kind,Instruction=kind==action.Kind?action.Instruction:null,
                ForkInstruction=kind!=action.Kind&&action.ForkInstruction is {} original?original with{Kind=kind,Text=null}:action.ForkInstruction,
                DistanceMeters=new LaneActionInterval(entry-remembered.Lead,entry).AtFraction(.1),EndDistanceMeters=entry,ApproachInterval=new(entry-remembered.Lead,entry)};
        }).Where(a=>a.EndDistanceMeters is null or >=0).OrderBy(a=>a.DistanceMeters).ToArray();
    }
    private static double Length(DirectedRoad road)=>RouteGeometry.Length(road);
}
