using System.Numerics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Core;

public enum ComponentContinuityKind { Main, Hard, Soft, Natural, Singular, Unknown, NeedsMapObjects }
public enum ComponentLaneFate { Continues, Dies, Separates, Unresolved, Born, Joins }
public enum ComponentTurnKind { Forward, Turn, Back, Unknown }
public sealed record ComponentTurn(NavigationSide Side,ComponentTurnKind Kind,string? Failure=null);

public enum ComponentBoundaryInterface { None, Start, End }
/// <summary>A point on a particular directed physical passage, with optional proven interface identity.</summary>
public sealed record ComponentBoundary(string EdgeId,double OffsetMeters,Vector3 Position)
{
    public ulong StartNodeUid {get;init;}
    public ulong EndNodeUid {get;init;}
    public ComponentBoundaryInterface Interface {get;init;}
    public ulong? InterfaceNodeUid=>Interface switch
    {ComponentBoundaryInterface.Start when StartNodeUid!=0=>StartNodeUid,
        ComponentBoundaryInterface.End when EndNodeUid!=0=>EndNodeUid,_=>null};
}

/// <summary>One original lane's static fate, independent of the player's chosen route.</summary>
public sealed record ComponentLaneOutcome(int EntryOrder,int ExitOrder,ComponentLaneFate Fate,
    bool InComponent,double TerminalDistanceFromExitMeters,
    ComponentBoundary InitialMidpoint,ComponentBoundary InitialExit,
    ComponentBoundary? Terminal,ComponentBoundary? LifecycleEnd,
    ComponentBoundary? SoftAction,ComponentTurn? Turn=null,MapQueryIssue? Issue=null)
{
    public IReadOnlyList<int> CurveIndices {get;init;}=[];
    public double MidpointDistanceFromExitMeters {get;init;}
    public double LifecycleDistanceFromExitMeters {get;init;}
    public double SoftActionDistanceFromExitMeters {get;init;}
    public string? StructuralConstraintId {get;init;}
    public ulong? TurnPrefabUid {get;init;}
    public int? TurnSharedCurveIndex {get;init;}
    public int? TurnBranchCurveIndex {get;init;}
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
        IReadOnlyList<ComponentLaneOutcome> lanes,int legalExitCount)
    {
        ulong uid=edge.LanePaths.FirstOrDefault()?.PrefabUid??0;
        int input=edge.LanePaths.Select(p=>p.EntryLaneCount).DefaultIfEmpty(0).Max();
        int output=edge.LanePaths.Select(p=>p.ExitLaneCount).DefaultIfEmpty(0).Max();
        ComponentPassageContinuity Result(ComponentContinuityKind kind,ComponentBoundary? action=null,
            ComponentBoundary? end=null,ComponentBoundary? start=null,ComponentBoundary? finish=null,MapQueryIssue? issue=null,ComponentTurn? turn=null)
        {
            var constraints=lanes.Select(l=>l.StructuralConstraintId).OfType<string>().Distinct().Order().ToArray();
            var sides=lanes.Select(l=>l.Turn?.Side).Where(s=>s is NavigationSide.Left or NavigationSide.Right).Distinct().ToArray();
            var offered=kind is ComponentContinuityKind.Main or ComponentContinuityKind.Natural
                ?lanes.Where(l=>l.Fate==ComponentLaneFate.Continues):lanes;
            var entry=offered.Select(l=>l.EntryOrder).Distinct().Order().ToArray();
            var exit=offered.Select(l=>l.ExitOrder).Distinct().Order().ToArray();
            if((kind is ComponentContinuityKind.Hard or ComponentContinuityKind.Soft)&&sides.Length>1)
            {turn=null;issue=new MapQueryFailure(MapQueryFailureKind.Ambiguous,"同一组件入口的车道动作方向冲突");}
            return new(edge.Id,uid,edge.StartNodeUid,edge.EndNodeUid,isMain,kind,action,end,start,finish,
                entry,exit,input,output,legalExitCount,
                turn,
                Array.AsReadOnly(lanes.ToArray()),issue??lanes.Select(l=>l.Issue).OfType<MapQueryIssue>().FirstOrDefault())
            {StructuralConstraintId=constraints.Length==0?null:string.Join('|',constraints)};
        }
        if(lanes.Count==0)return Result(ComponentContinuityKind.Unknown,issue:new MapQueryFailure(MapQueryFailureKind.InvalidData,"组件没有原生车道结果"));
        if(lanes.All(l=>l.InComponent&&l.Fate==ComponentLaneFate.Separates))
            return Result(ComponentContinuityKind.Singular,issue:new MapQueryFailure(MapQueryFailureKind.Ambiguous,"奇异入口：全部车道在汇入组件内分离"));
        if(lanes.Any(l=>l.Fate==ComponentLaneFate.Unresolved))
            return Result(ComponentContinuityKind.Unknown,issue:lanes.Select(l=>l.Issue).OfType<MapQueryIssue>().FirstOrDefault()
                ??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"组件车道来源或主线资格未确认"));
        bool hard=lanes.All(l=>l.InComponent&&l.Fate==ComponentLaneFate.Dies);
        bool soft=!hard&&lanes.All(l=>(l.Fate is ComponentLaneFate.Dies or ComponentLaneFate.Separates)
            &&l.TerminalDistanceFromExitMeters<=WindowMeters);
        if(hard)
        {
            var first=lanes.MinBy(l=>l.TerminalDistanceFromExitMeters)!;
            var last=lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!;
            return Result(ComponentContinuityKind.Hard,first.Terminal,last.LifecycleEnd,turn:first.Turn);
        }
        if(soft)
        {
            double CandidateDistance(ComponentLaneOutcome lane)=>lane.InComponent&&lane.Fate==ComponentLaneFate.Dies
                ?lane.TerminalDistanceFromExitMeters:lane.SoftActionDistanceFromExitMeters;
            var first=lanes.MinBy(CandidateDistance)!;
            var last=lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!;
            var terminal=lanes.MaxBy(l=>l.TerminalDistanceFromExitMeters)!;
            return Result(ComponentContinuityKind.Soft,first.InComponent&&first.Fate==ComponentLaneFate.Dies?first.Terminal:first.SoftAction,last.LifecycleEnd,
                lanes.First().InitialExit,terminal.Terminal,turn:first.Turn);
        }
        if(isMain)return Result(ComponentContinuityKind.Main,
            lanes.MinBy(l=>l.MidpointDistanceFromExitMeters)!.InitialMidpoint,
            lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!.InitialExit);
        return Result(ComponentContinuityKind.Natural,
            lanes.MinBy(l=>l.MidpointDistanceFromExitMeters)!.InitialMidpoint,
            lanes.MaxBy(l=>l.LifecycleDistanceFromExitMeters)!.InitialExit);
    }

    public static ComponentPassageContinuity AggregateOutlet(DirectedRoad edge,bool isMain,
        IReadOnlyList<ComponentLaneOutcome> lanes,int legalExitCount)
    {
        var path=edge.LanePaths[0];
        var inherited=lanes.Where(l=>l.Fate==ComponentLaneFate.Continues).ToArray();
        var uncertain=lanes.FirstOrDefault(l=>l.Fate==ComponentLaneFate.Unresolved);
        var soft=lanes.Where(l=>!l.InComponent&&(l.Fate is ComponentLaneFate.Born or ComponentLaneFate.Joins)).ToArray();
        var hard=lanes.Where(l=>l.InComponent&&l.Fate==ComponentLaneFate.Born).ToArray();
        ComponentPassageContinuity Result(ComponentContinuityKind kind,IReadOnlyList<ComponentLaneOutcome> dominant,
            ComponentLaneOutcome? owner,ComponentBoundary? action=null,ComponentBoundary? start=null,ComponentBoundary? end=null,
            MapQueryIssue? issue=null)
        {
            var directions=dominant.Select(l=>l.Turn?.Side).Where(s=>s is NavigationSide.Left or NavigationSide.Right).Distinct().ToArray();
            if(directions.Length>1)issue=new MapQueryFailure(MapQueryFailureKind.Ambiguous,"Outlet lanes disagree on turn side.")
                {QueryAnchor=edge.Points[0]};
            var constraints=dominant.Select(l=>l.StructuralConstraintId).OfType<string>().Distinct().Order().ToArray();
            return new(edge.Id,path.PrefabUid,edge.StartNodeUid,edge.EndNodeUid,isMain,kind,action,owner?.InitialExit,start,end,
                dominant.Select(l=>l.EntryOrder).Distinct().Order().ToArray(),dominant.Select(l=>l.ExitOrder).Distinct().Order().ToArray(),
                path.EntryLaneCount,path.ExitLaneCount,legalExitCount,issue is null?owner?.Turn:null,lanes,issue)
                {StructuralConstraintId=constraints.Length==0?null:string.Join('|',constraints)};
        }
        if(inherited.Length>0)
        {
            var first=inherited.MinBy(l=>l.InitialMidpoint.OffsetMeters)!;
            return Result(isMain?ComponentContinuityKind.Main:ComponentContinuityKind.Natural,inherited,first,first.InitialMidpoint);
        }
        if(uncertain is not null)return Result(ComponentContinuityKind.Unknown,[],null,
            issue:uncertain.Issue??new MapQueryFailure(MapQueryFailureKind.Ambiguous,"Outlet lane origin is unresolved.")
                {QueryAnchor=edge.Points[0]});
        if(soft.Length>0)
        {
            var first=soft.MinBy(l=>l.SoftActionDistanceFromExitMeters)!;
            var earliest=soft.MinBy(l=>l.TerminalDistanceFromExitMeters)!;
            var approachEnd=new ComponentBoundary(edge.Id,0,edge.Points[0])
                {StartNodeUid=edge.StartNodeUid,EndNodeUid=edge.EndNodeUid,Interface=ComponentBoundaryInterface.Start};
            return Result(ComponentContinuityKind.Soft,soft,first,first.SoftAction,earliest.Terminal,approachEnd,first.Issue);
        }
        var firstHard=hard.MinBy(l=>l.Terminal!.OffsetMeters)!;
        return Result(ComponentContinuityKind.Hard,hard,firstHard,firstHard.Terminal);
    }

}
