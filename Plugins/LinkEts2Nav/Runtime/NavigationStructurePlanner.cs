using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Runtime;

/// <summary>Immutable route facts in one component-local coordinate system. No vehicle progress is stored here.</summary>
internal sealed record NavigationStructurePlan(IReadOnlyList<DirectedRoad> Route,
    IReadOnlyList<NavigationManeuver> Actions,LaneNavigationPlan Lanes,IReadOnlyList<LaneMergeObservation> Merges,
    IReadOnlyList<RoadForkDiagnostic> Forks,double ConfirmedUntil,bool Unresolved,double UnresolvedAlong)
{
    public IReadOnlyList<RoundaboutRouteRange> RoundaboutRanges {get;init;}=[];
    public AuxiliaryLaneIndex? Auxiliaries {get;init;}
    public LaneContractionView? Contractions {get;init;}
    public IReadOnlyList<NavigationUnresolvedSpan> UnresolvedSpans {get;init;}=[];
    public IReadOnlyList<NavigationUnresolvedSpan> OrdinaryUnresolvedSpans {get;init;}=[];
    /// <summary>Past ambiguity does not erase a later confirmed stretch. Active or future ambiguity remains a boundary.</summary>
    public double ConfirmationEndAfter(double boundary)
        =>Math.Min(ConfirmedUntil,UnresolvedSpans.FirstOrDefault(s=>s.EndMeters>boundary)?.StartMeters??double.PositiveInfinity);
}

/// <summary>Runs only when route topology or loaded map facts change.</summary>
internal sealed class NavigationStructurePlanner(IReadOnlySet<string>? spokenPlaceNames=null,Action<string>? log=null)
{
    private readonly NavigationManeuverDetector detector=new();
    private readonly LaneMergeDetector mergeDetector=new();

    public NavigationStructurePlan Build(IReadOnlyList<DirectedRoad> route,RouteSnapshot sdkRoute,NearbyMapSnapshot nearby,RoadPathSource paths,LaneNavigationPlan lanes,AuxiliaryLaneIndex? auxiliaries=null)
    {
        var ranges=RoundaboutRouteAdapter.Build(route,nearby.RoadStructures);
        detector.MissingConnection=paths.DescribeMissingConnection;
        if(nearby.Components is {} components)detector.Components.UseIndex(paths.GetNavigationGraph(),components);
        IReadOnlyList<NavigationManeuver> upcoming=detector.FindSequence(route,0,paths.GetNavigationGraph(),out double confirmedUntil,out bool unresolvedJoin,out double firstUnresolvedJoinAlong,lanes,ranges);
        if(NavigationDestination.Find(sdkRoute,route,0,confirmedUntil,unresolvedJoin) is {} destination)
        {
            var terminal=sdkRoute.Nodes.MinBy(n=>n.DistanceToEndMeters)!;
            if(nearby.Companies.AtTerminal(terminal.Uid) is {DisplayName:{} name})
            {
                destination=destination with{DestinationCompanyName=name,ExecutionInstruction="到达"+name+"货场附近，本次导航结束"};
                log?.Invoke("导航终点已按预制件归属确认货场名称："+name+
                    (spokenPlaceNames?.Contains(name)==true?"；名称音频已准备。":"；缺少该名称的预生成音频，播放时使用通用目的地提示。"));
            }
            upcoming=upcoming.Append(destination).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        foreach(var leg in nearby.Ferries?.OnRoute(sdkRoute)??[])
        {
            double offset=0;double? entry=null;
            foreach(var edge in route)
            {
                if(edge.StartNodeUid==leg.EntranceUid){entry=offset;break;}
                offset+=RouteGeometry.Length(edge);
                if(edge.EndNodeUid==leg.EntranceUid){entry=offset;break;}
            }
            if(entry is not {} at)continue;
            upcoming=upcoming.Append(new NavigationManeuver(leg.Id,ManeuverKind.BoardFerry,at,route[^1].Id,route[^1].Id)
            {
                EndDistanceMeters=at,JunctionId="prefab:"+leg.Source.PrefabUid.ToString("X16"),
                Instruction="进入码头",NearInstruction=FerryNavigationPrompts.Passage(leg.Source.Name,leg.Target.Name,spokenPlaceNames)
            }).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        foreach(var issue in detector.Components.Issues)paths.ReportMapIssue(issue);
        var merges=mergeDetector.Find(route,paths.GetNavigationGraph(),lanes,paths.DescribeMissingConnection)
            .Where(m=>!ranges.Any(range=>range.ContainsVisit(m.SourceVisitIndex))).ToArray();
        foreach(var issue in merges.Select(m=>m.Origin?.MapIssue).OfType<MapQueryIssue>())paths.ReportMapIssue(issue);
        upcoming=ActionPointConstraints.Apply(LaneContractionGuidance.Apply(upcoming,merges));
        var issues=detector.ForkDiagnostics.Concat(merges.Where(m=>m.Kind==LaneMergeKind.Uncertain)
            .Select(m=>new RoadForkDiagnostic($"prefab:{m.PrefabUid:X16}",m.EndingEntryNode??0,0,m.Reason))).Distinct().ToArray();
        var unresolved=detector.UnresolvedSpans.OrderBy(s=>s.StartMeters).ToArray();
        return new NavigationStructurePlan(route,upcoming,lanes,merges,issues,confirmedUntil,
            unresolvedJoin,firstUnresolvedJoinAlong){RoundaboutRanges=ranges,Auxiliaries=auxiliaries,
                UnresolvedSpans=unresolved,OrdinaryUnresolvedSpans=detector.OrdinaryUnresolvedSpans};
    }
}
