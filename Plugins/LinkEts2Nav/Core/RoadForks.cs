namespace LinkEts2Nav.Core;

public enum ForkSignal { None, Left, Right }
public sealed record NativeForkDecision(int EntryLane,int FromCurve,int ToCurve);
public sealed record RoadForkBranch(ulong ExitNode,ForkSignal? Signal,IReadOnlyList<int> EntryLanes,
    IReadOnlyList<int> EntryOrders,IReadOnlyList<NativeForkDecision> Decisions,bool HasInheritedLane,string? Failure=null);
public sealed record RoadForkInfo(ulong EntryNode,int EntryLaneCount,IReadOnlyList<RoadForkBranch> Branches,string? Failure=null);
public sealed record RoadForkInstruction(ManeuverKind Kind,bool Silent,string? Text,IReadOnlyList<int> EntryOrders,
    IReadOnlyList<int> DedicatedOrders,string? Failure=null);
public sealed record RoadForkDiagnostic(string Prefab,ulong EntryNode,ulong ExitNode,string Reason);

/// <summary>Ordinary road splits use native branch lamps and lane reachability, never exit angles.</summary>
public static class RoadForkRules
{
    public static RoadForkInstruction Decide(RoadForkInfo info,ulong exit)
    {
        RoadForkInstruction Unknown(string reason)=>new(ManeuverKind.Straight,true,null,[],[],reason);
        if(info.Failure is {} failure)return Unknown(failure);
        var branch=info.Branches.SingleOrDefault(b=>b.ExitNode==exit);
        if(branch is null)return Unknown("导航出口不在完整原生分流集合中");
        if(branch.Signal is null)return Unknown(branch.Failure??"目标分支灯光不一致");
        var otherOrders=info.Branches.Where(b=>b.ExitNode!=exit).SelectMany(b=>b.EntryOrders).ToHashSet();
        var dedicated=branch.EntryOrders.Where(o=>!otherOrders.Contains(o)).Order().ToArray();
        if(branch.Signal is ForkSignal.Left or ForkSignal.Right)
            return new(branch.Signal==ForkSignal.Left?ManeuverKind.KeepLeft:ManeuverKind.KeepRight,false,null,branch.EntryOrders,dedicated);
        if(info.Branches.Any(b=>b.Signal is null))return Unknown("其他出口灯光不确定，无法完整统计无灯分支");
        var unlit=info.Branches.Where(b=>b.Signal==ForkSignal.None).ToArray();
        if(unlit.Length==1)return new(ManeuverKind.Straight,true,null,branch.EntryOrders,dedicated);
        if(unlit.Any(b=>!b.HasInheritedLane))return Unknown("无灯分支缺少可独立验证的继承入口车道");
        var text=LaneSelectionText(info.EntryLaneCount,branch.EntryOrders);
        if(text is null)return Unknown("所有入口车道均可达目标无灯分支，无法用选道区别出口");
        return new(ManeuverKind.ChooseLanes,false,text,branch.EntryOrders,dedicated);
    }

    public static string? LaneSelectionText(int count,IReadOnlyList<int> selected)
    {
        var lanes=selected.Distinct().Order().ToArray();
        if(count<1||lanes.Length==0||lanes.Any(l=>l<0||l>=count)||lanes.Length==count)return null;
        bool contiguous=lanes[^1]-lanes[0]+1==lanes.Length;
        if(contiguous&&(lanes[0]==0||lanes[^1]==count-1))
        {
            string side=lanes[0]==0?"左":"右";
            if(lanes.Length==1)return "走"+(count==2?"":"最")+side+"侧车道";
            return "走"+side+"侧"+(lanes.Length==2?"两":NavigationPrompts.Number(lanes.Length))+"车道";
        }
        return "走右侧第"+string.Join("、第",lanes.Select(l=>count-l).Order().Select(NavigationPrompts.Number))+"车道";
    }
}
