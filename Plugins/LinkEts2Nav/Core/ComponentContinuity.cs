using System.Numerics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Core;

public enum ComponentContinuityKind { Main, Hard, Soft, Natural, Singular, Unknown }
public enum ComponentLaneFate { Continues, Dies, Separates, Unresolved }
public enum ComponentTurnKind { Forward, Turn, Back, Unknown }
public sealed record ComponentTurn(NavigationSide Side,ComponentTurnKind Kind,string? Failure=null);

/// <summary>A point on a particular directed physical passage, not merely a prefab UID.</summary>
public sealed record ComponentBoundary(string EdgeId,double OffsetMeters,Vector3 Position)
{
    public ulong StartNodeUid {get;init;}
    public ulong EndNodeUid {get;init;}
}

/// <summary>One original lane's static fate, independent of the player's chosen route.</summary>
public sealed record ComponentLaneOutcome(int EntryOrder,int ExitOrder,ComponentLaneFate Fate,
    bool InComponent,double TerminalDistanceFromExitMeters,
    ComponentBoundary InitialMidpoint,ComponentBoundary InitialExit,
    ComponentBoundary? Terminal,ComponentBoundary? LifecycleEnd,
    ComponentBoundary? SoftAction,ComponentTurn? Turn=null,MapQueryIssue? Issue=null)
{
    public double MidpointDistanceFromExitMeters {get;init;}
    public double LifecycleDistanceFromExitMeters {get;init;}
    public double SoftActionDistanceFromExitMeters {get;init;}
    public string? StructuralConstraintId {get;init;}
}

/// <summary>Static structure for one legal entry/exit passage. Consumers map its anchors onto the matching route visit.</summary>
public sealed record ComponentPassageContinuity(string EdgeId,ulong PrefabUid,ulong EntryNodeUid,ulong ExitNodeUid,
    bool IsMain,ComponentContinuityKind Kind,
    ComponentBoundary? ActionPoint,ComponentBoundary? LifecycleEnd,
    ComponentBoundary? IntervalStart,ComponentBoundary? IntervalEnd,
    IReadOnlyList<int> EntryOrders,IReadOnlyList<int> ExitOrders,
    int EntryLaneCount,int ExitLaneCount,int LegalExitCount,
    ComponentTurn? Turn,IReadOnlyList<ComponentLaneOutcome> Lanes,MapQueryIssue? Issue=null)
{
    public double? ExitTurnRadians {get;init;}
    public bool HasNaturalAlternative {get;init;}
    public string? StructuralConstraintId {get;init;}
}

/// <summary>Combines original lanes: action is the earliest, lifetime ends at the latest. No lane is removed from connectivity.</summary>
public static class ComponentContinuity
{
    public const double WindowMeters=500;
    public const double AdvanceMeters=200;

    public static ComponentPassageContinuity Aggregate(DirectedRoad edge,bool isMain,
        IReadOnlyList<ComponentLaneOutcome> lanes,int legalExitCount,bool outgoing=false)
    {
        ulong uid=edge.LanePaths.FirstOrDefault()?.PrefabUid??0;
        int input=edge.LanePaths.Select(p=>p.EntryLaneCount).DefaultIfEmpty(0).Max();
        int output=edge.LanePaths.Select(p=>p.ExitLaneCount).DefaultIfEmpty(0).Max();
        var entry=lanes.Select(l=>l.EntryOrder).Distinct().Order().ToArray();
        var exit=lanes.Select(l=>l.ExitOrder).Distinct().Order().ToArray();
        ComponentPassageContinuity Result(ComponentContinuityKind kind,ComponentBoundary? action=null,
            ComponentBoundary? end=null,ComponentBoundary? start=null,ComponentBoundary? finish=null,MapQueryIssue? issue=null,ComponentTurn? turn=null)
        {
            var constraints=lanes.Select(l=>l.StructuralConstraintId).OfType<string>().Distinct().Order().ToArray();
            var sides=lanes.Select(l=>l.Turn?.Side).Where(s=>s is NavigationSide.Left or NavigationSide.Right).Distinct().ToArray();
            if(kind is ComponentContinuityKind.Hard or ComponentContinuityKind.Soft&&sides.Length>1)
            {turn=null;issue=new MapQueryFailure(MapQueryFailureKind.Ambiguous,"同一组件入口的车道动作方向冲突");}
            return new(edge.Id,uid,edge.StartNodeUid,edge.EndNodeUid,isMain,kind,action,end,start,finish,
                entry,exit,input,output,legalExitCount,
                turn,
                Array.AsReadOnly(lanes.ToArray()),issue??lanes.Select(l=>l.Issue).OfType<MapQueryIssue>().FirstOrDefault())
            {StructuralConstraintId=constraints.Length==0?null:string.Join('|',constraints)};
        }
        if(lanes.Count==0)return Result(ComponentContinuityKind.Unknown,issue:new MapQueryFailure(MapQueryFailureKind.InvalidData,"组件没有原生车道结果"));
        if(isMain)return Result(ComponentContinuityKind.Main,
            (outgoing?lanes.MaxBy(l=>l.MidpointDistanceFromExitMeters):lanes.MinBy(l=>l.MidpointDistanceFromExitMeters))!.InitialMidpoint,
            lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!.InitialExit);
        if(lanes.All(l=>l.InComponent&&l.Fate==ComponentLaneFate.Separates))
            return Result(ComponentContinuityKind.Singular,issue:new MapQueryFailure(MapQueryFailureKind.Ambiguous,"奇异入口：全部车道在汇入组件内分离"));
        if(lanes.Any(l=>l.Fate==ComponentLaneFate.Unresolved))
            return Result(ComponentContinuityKind.Unknown,issue:lanes.Select(l=>l.Issue).OfType<MapQueryIssue>().FirstOrDefault()
                ??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"组件车道来源或主线资格未确认"));
        bool hard=lanes.All(l=>l.InComponent&&l.Fate==ComponentLaneFate.Dies);
        bool soft=!hard&&lanes.All(l=>l.Fate is ComponentLaneFate.Dies or ComponentLaneFate.Separates
            &&l.TerminalDistanceFromExitMeters<=WindowMeters);
        if(hard)
        {
            var first=outgoing?lanes.MaxBy(l=>l.TerminalDistanceFromExitMeters)!:lanes.MinBy(l=>l.TerminalDistanceFromExitMeters)!;
            var last=lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!;
            return Result(ComponentContinuityKind.Hard,first.Terminal,last.LifecycleEnd,turn:first.Turn);
        }
        if(soft)
        {
            double CandidateDistance(ComponentLaneOutcome lane)=>lane.InComponent&&lane.Fate==ComponentLaneFate.Dies
                ?lane.TerminalDistanceFromExitMeters:lane.SoftActionDistanceFromExitMeters;
            var first=outgoing?lanes.MaxBy(CandidateDistance)!:lanes.MinBy(CandidateDistance)!;
            var last=lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!;
            var terminal=lanes.MaxBy(l=>l.TerminalDistanceFromExitMeters)!;
            return Result(ComponentContinuityKind.Soft,first.InComponent&&first.Fate==ComponentLaneFate.Dies?first.Terminal:first.SoftAction,last.LifecycleEnd,
                lanes.First().InitialExit,terminal.Terminal,turn:first.Turn);
        }
        return Result(ComponentContinuityKind.Natural,
            (outgoing?lanes.MaxBy(l=>l.MidpointDistanceFromExitMeters):lanes.MinBy(l=>l.MidpointDistanceFromExitMeters))!.InitialMidpoint,
            lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!.InitialExit);
    }
}
