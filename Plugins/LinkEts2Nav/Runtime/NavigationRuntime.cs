using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record NavigationDiagnostic(string State, NavigationManeuver? Next = null, string? LastSpoken = null)
{
    public IReadOnlyList<LaneMergeObservation> LaneMerges { get; init; } = [];
    public IReadOnlyList<RoadForkDiagnostic> RoadForks { get; init; } = [];
    public LaneNavigationPlan? LaneRoute { get; init; }
    public IReadOnlyList<NavigationManeuver> Upcoming { get; init; } = [];
    public double? ConfirmedAheadMeters { get; init; }
    public bool DeferredUntilJunctionExit { get; init; }
}

/// <summary>Collects route events for the shared coordinator. Standalone callers only prepare and inspect due text; they never play audio.</summary>
public sealed class NavigationRuntime(SpeechAnnouncementQueue? speech = null,
    Action<string>? log = null,GuidanceCoordinator? guidance=null,IReadOnlySet<string>? spokenCityNames=null)
{
    private readonly NavigationStructurePlanner structurePlanner = new(spokenCityNames,log);
    private readonly FerryNavigationProgress ferryProgress=new();
    public FerryTravelExpectation? ExpectedFerry=>ferryProgress.Expected;
    private readonly LaneRoutePlanner lanePlanner = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<DirectedRoad>,NavigationStructurePlan> structures = new();
    private NavigationStructurePlan? publishedStructure;
    private long publishedGeneration=-1;
    private NavigationMapIdentity? mapIdentity;
    private int routeBuildGeneration;
    private string? laneSession;
    private long laneGeneration=-1;
    private long structureFirstNodeOrdinal;
    private double routeOrigin;
    public int StructureBuildCount { get; private set; }
    public int EventPlanBuildCount { get; private set; }
    /// <summary>Reword future named events without resetting progress or consumed triggers.</summary>
    public void RefreshSpeechNames(){structures.Clear();publishedStructure=null;}
    internal GuidanceCoordinator Coordinator { get; } = guidance ?? GuidanceCoordinator.ForAnalysis(speech);
    private string? planningSession;
    private readonly NavigationEventPlanner eventPlanner=new();
    private readonly RouteProgressTracker progress=new();
    private readonly EnvironmentPreviewPlanner cityPlanner=new();
    private readonly EnvironmentPreviewPlanner narrowPlanner=new();
    private readonly CityEntryEventBuilder cityEntries=new(log,spokenCityNames);
    private string? citySession;
    private string? cityFrame;
    private TruckLib.ScsMap.Map? map;
    private RoadPathSource? paths;
    private string? lastSpoken, signature;
    private string? connectionFailureSignature;
    private DateTimeOffset reportedAt;

    public NavigationDiagnostic Process(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        if (guidance is not null) return Collect(data, nearby, now);
        Coordinator.Observe(data, now);
        if (Coordinator.LastQueued("navigation") is null) lastSpoken = null;
        var diagnostic = Collect(data, nearby, now);
        Coordinator.Tick(data, now);
        foreach (var due in Coordinator.LatestTriggers.Where(r => r.Source == "navigation" && r.Disposition == TriggerDisposition.Queued)
                     .OrderBy(r => r.Trigger.Priority))
        {
            lastSpoken = due.Trigger.Text;
            log?.Invoke("导航指令：" + lastSpoken + " 当前路口=" + diagnostic.Upcoming.FirstOrDefault()?.Id + "；下一动作=" + diagnostic.Next?.Id);
        }
        return diagnostic with { LastSpoken = lastSpoken };
    }

    private NavigationDiagnostic Collect(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        var stable=Coordinator.RouteFor(data);
        ferryProgress.Observe(data,nearby,now);
        if(ferryProgress.TakeEntryPassage() is {} entered)
            Coordinator.ObservePassage("navigation",entered,now);
        var vehicle = data.Vehicle;
        if (!data.IsVehicleUsable || vehicle is not { SdkActive: true, Paused: false, IsFinite: true }
            || vehicle.SpeedMetersPerSecond < -.1 || now < vehicle.ReceivedAt || now - vehicle.ReceivedAt > TimeSpan.FromSeconds(2))
            return Suspend("等待有效的行驶数据。", now);
        if (data.RouteAvailability != RouteAvailability.Available)
        {
            if(data.RouteAvailability == RouteAvailability.None)paths?.ResetObservedLanes();
            return Suspend(data.RouteAvailability == RouteAvailability.None
                ? "未设置游戏导航路线，转向提示暂挂。" : "等待有效的游戏导航路线。", now);
        }
        if (mapIdentity is null || !mapIdentity.Matches(nearby))
        {
            mapIdentity=new(nearby);map=nearby.Map;
            paths?.Dispose();
            paths=new RoadPathSource(nearby,log);
            structures.Clear();publishedStructure=null;routeBuildGeneration=0;
        }
        var sdkRoute=stable.FullRoute;
        if(laneSession!=data.SessionId || laneGeneration!=stable.GeometryGeneration)
        {
            paths!.ResetObservedLanes();
            laneSession=data.SessionId;laneGeneration=stable.GeometryGeneration;
        }
        if(structureFirstNodeOrdinal!=stable.FirstNodeOrdinal)
        {
            structures.Clear();publishedStructure=null;structureFirstNodeOrdinal=stable.FirstNodeOrdinal;
        }
#if DEBUG
        if(sdkRoute.Availability!=RouteAvailability.Available)
            throw new NavigationContractException("Available navigation requires the atomically captured stable route.");
#endif
        int currentNode=stable.CurrentNodeIndex;
        string routeRevision=stable.CurrentSnapshot.Revision;
        var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
        var components=paths!.GetRouteComponents(sdkRoute);
        if(routeBuildGeneration!=paths.RouteStructureBuildCount)
        {
            structures.Clear();publishedStructure=null;routeBuildGeneration=paths.RouteStructureBuildCount;
        }
        // Every known SDK component is planned in full before position matching.
        // Unknown map gaps keep their separate coordinate frames; they are never
        // bridged by a player-dependent route or a fabricated distance.
        NavigationStructurePlan StructureFor(IReadOnlyList<DirectedRoad> component)
        {
            if(structures.TryGetValue(component,out var known))return known;
            var selectedLanes=lanePlanner.Build(component);
            var built=structurePlanner.Build(component,sdkRoute,nearby,paths,selectedLanes,paths.SourceNodeIndices(component),firstNodeOrdinal:stable.FirstNodeOrdinal);
            structures.Add(component,built);
            StructureBuildCount++;
            return built;
        }
        foreach(var component in components)StructureFor(component);
        var (route, along) = paths!.MatchRoutePosition(position, vehicle.ForwardDirection, sdkRoute, currentNode);
        bool yardApproach=false;double exitDistance=0;
        if(along is null)
        {
            var areas=nearby.FreeNavigationAreas.Where(a=>a.Contains(position)).ToArray();
            var remaining=stable.CurrentSnapshot.Nodes;
            int firstIndex=Enumerable.Range(0,remaining.Count).MaxBy(i=>remaining[i].DistanceToEndMeters);
            var first=remaining[firstIndex];
            if(areas.Length==1 && areas[0].ExitNodes.Contains(first.Uid))
            {
                var entry=paths.GetRouteEntry(sdkRoute,stable.CurrentNodeIndex+firstIndex);
                if(entry is not null && entry.Route[entry.VisitIndex].Id.StartsWith("prefab:",StringComparison.Ordinal))
                {
                    var exit=entry.Route[entry.VisitIndex].EntryInterface;var delta=exit-position;
                    exitDistance=new Vector2(delta.X,delta.Z).Length();
                    if(exitDistance>0 && exitDistance<=100 && Math.Abs(delta.Y)<=3
                        && Vector3.Dot(Vector3.Normalize(new(delta.X,0,delta.Z)),vehicle.ForwardDirection)>=.5f)
                    { route=entry.Route;along=entry.AlongMeters-exitDistance;yardApproach=true; }
                }
            }
        }
        if (along is null) return Suspend("当前位置未唯一匹配连续导航道路，暂不提示转向。", now);
        var structure=StructureFor(route);
        if(!ReferenceEquals(publishedStructure,structure))
        {
            ReportConnectionFailure(structure.Route,structure.ConfirmedUntil,structure.Unresolved,structure.UnresolvedAlong);
            foreach(var issue in structure.Forks)
                log?.Invoke($"道路分流未定：{issue.Prefab}，入口={issue.EntryNode:X16}，出口={issue.ExitNode:X16}；{issue.Reason}");
        }
        var laneRoute=structure.Lanes;
        bool structuralUpdate=!ReferenceEquals(publishedStructure,structure)||planningSession!=data.SessionId||publishedGeneration!=stable.GeometryGeneration;
        if(structuralUpdate)
        {
            double? knownOrigin=publishedStructure is {} previous && planningSession==data.SessionId
                && publishedGeneration==stable.GeometryGeneration && paths.IsSameSdkComponent(previous.Route,route)?routeOrigin:null;
            routeOrigin=progress.Update(data.SessionId,route,0,knownOrigin);
        }
        double playerAlong=routeOrigin+along.Value;
        var upcoming=structure.Actions.Select(m=>Relative(m,along.Value)).Where(m=>m.DistanceMeters>=0).ToArray();
        IReadOnlyList<NavigationManeuver> plannedActions=structuralUpdate && structure.RoundaboutRanges.Count>0
            ?[..structure.Actions.Where(m=>m.HasSdkVisitIdentity && m.DistanceMeters<along.Value).Select(m=>Relative(m,along.Value)),..upcoming]
            :upcoming;
        var merges=structure.Merges.Select(m=>m with{DistanceMeters=m.DistanceMeters-along.Value,
            EndDistanceMeters=m.EndDistanceMeters-along.Value}).Where(m=>m.DistanceMeters>=0).ToArray();
        bool confirmedContinuation=!yardApproach&&!upcoming.Any(NavigationEventPlanner.IsNavigationAction);
        double confirmedAhead=Math.Max(0,structure.ConfirmationEndAfter(along.Value)-along.Value);
        var next=upcoming.FirstOrDefault(NavigationEventPlanner.IsNavigationAction);
        var currentRing=structure.RoundaboutRanges.SingleOrDefault(r=>
            (along>r.StartMeters||r.StartsInside&&along==r.StartMeters)&&along<r.EndMeters);
        var currentJunction=yardApproach?null:currentRing is {} ring?"roundabout:"+ring.RingId:CurrentJunction(structure,along.Value);
        double? lastActionEnd=structure.Actions.LastOrDefault(NavigationEventPlanner.IsNavigationAction)?.EndDistanceMeters;
        double continuationEnd=structure.ConfirmationEndAfter(lastActionEnd??along.Value);
        double? componentEnd=structure.Actions.Where(NavigationEventPlanner.IsNavigationAction)
            .Where(m=>m.DistanceMeters<along&&m.EndDistanceMeters>along).Select(m=>m.EndDistanceMeters).FirstOrDefault();
        if(!yardApproach&&currentRing is {StartsInside:true})componentEnd=currentRing.EndMeters;
        bool defer=componentEnd is not null&&currentJunction is not null&&next is not null&&currentJunction!=next.JunctionId;
        if(citySession!=data.SessionId){cityEntries.Reset();citySession=data.SessionId;}
        if(cityFrame!=progress.Frame)
        {
            Coordinator.ClearSource(CityEntryEventBuilder.Kind);cityPlanner.Reset();Coordinator.ClearSource(NarrowRoadEventBuilder.Kind);
            narrowPlanner.Reset();cityFrame=progress.Frame;
        }
        if(structuralUpdate)
        {
            // Only newly discovered structure can create/re-anchor speech points.
            // Ordinary progress never calls any event planner or geometry recognizer.
            double? initialEnd=componentEnd is {} end?routeOrigin+end:null;
            bool tail=!yardApproach&&(lastActionEnd??0)<continuationEnd;
            Coordinator.Publish(eventPlanner.Build(data.SessionId,routeRevision,vehicle.ReceivedAt,
                playerAlong,plannedActions,true,false,tail,Coordinator.Scheduler,progress.Frame,initialEnd,
                Coordinator.WillReplaceSource("navigation",routeRevision),routeOrigin+continuationEnd,stable.ContinuityGeneration));
            var previousCityRoute=planningSession==data.SessionId&&publishedGeneration==stable.GeometryGeneration?publishedStructure?.Route:null;
            if(previousCityRoute is not null&&(!route.Any(e=>e.ObservedLane is not null)
                ||!paths.SourceNodeIndices(previousCityRoute).SequenceEqual(paths.SourceNodeIndices(route))))previousCityRoute=null;
            var entries=cityEntries.Build(route,playerAlong,nearby.SpeedLimits,routeOrigin,nearby.PrefabPaths,previousCityRoute,out var removedEntries)
                .Select(e=>e with{Id=e.Id+"#"+progress.Frame}).ToArray();
            var withdrawn=removedEntries.Select(id=>id+"#"+progress.Frame).ToArray();
            foreach(string id in withdrawn)cityPlanner.Withdraw(id);
            if(Coordinator.WillReplaceSource(CityEntryEventBuilder.Kind,routeRevision))
                cityPlanner.Reconcile(entries.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,Coordinator.Scheduler,CityEntryEventBuilder.Kind);
            var city=cityPlanner.Build(CityEntryEventBuilder.Kind,playerAlong,entries,Coordinator.Scheduler,true);
            Coordinator.Publish(new(CityEntryEventBuilder.Kind,data.SessionId,routeRevision,playerAlong,true,vehicle.ReceivedAt,city.Sequences)
                {CoordinateFrame=progress.Frame,ReplacedSequenceIds=withdrawn});
            var narrow=NarrowRoadEventBuilder.Build(route,nearby.SpeedLimits,routeOrigin)
                .Select(e=>e with{Id=e.Id+"#"+progress.Frame}).ToArray();
            if(Coordinator.WillReplaceSource(NarrowRoadEventBuilder.Kind,routeRevision))
                narrowPlanner.Reconcile(narrow.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,Coordinator.Scheduler,NarrowRoadEventBuilder.Kind);
            var narrowPlan=narrowPlanner.Build(NarrowRoadEventBuilder.Kind,playerAlong,narrow,Coordinator.Scheduler,true);
            Coordinator.Publish(new(NarrowRoadEventBuilder.Kind,data.SessionId,routeRevision,playerAlong,true,vehicle.ReceivedAt,narrowPlan.Sequences){CoordinateFrame=progress.Frame});
            EventPlanBuildCount++;
        }
        foreach(string source in new[]{"navigation",CityEntryEventBuilder.Kind,NarrowRoadEventBuilder.Kind})
            Coordinator.UpdateProgress(source,data.SessionId,playerAlong,vehicle.ReceivedAt,true);
        if(guidance is not null)lastSpoken=Coordinator.LastPlayed("navigation");
        publishedStructure=structure;planningSession=data.SessionId;publishedGeneration=stable.GeometryGeneration;
        string state = yardApproach ? "货场出口方向提示（距出口采用直线距离）。" : defer ? "正在通过当前路口。" : next is not null ? "沿游戏导航路线提供基础路口提示。"
            : confirmedContinuation ? "当前可见路线没有非直行动作，沿当前道路行驶，等待下一操作提示。"
            : "当前连续路径内没有可确认的普通路口操作。";
        Report(state, next, now);
        return new(state, next, lastSpoken) { LaneRoute = laneRoute, LaneMerges = merges, RoadForks=structure.Forks, Upcoming = upcoming, ConfirmedAheadMeters = confirmedAhead, DeferredUntilJunctionExit = defer };
    }

    private static NavigationManeuver Relative(NavigationManeuver m,double along)=>m with
    {
        DistanceMeters=m.DistanceMeters-along,EndDistanceMeters=m.EndDistanceMeters-along,
        SpeechSequenceStartDistanceMeters=m.SpeechSequenceStartDistanceMeters-along,
        ApproachInterval=m.ApproachInterval is {} interval?new(interval.StartMeters-along,interval.EndMeters-along):null
    };

    // Missing map tails are normal. A failure inside the already constructed
    // continuous graph is different: retain the physical edge identities for replay diagnosis.
    private void ReportConnectionFailure(IReadOnlyList<DirectedRoad> route,double confirmedUntil,
        bool unresolved,double unresolvedAlong)
    {
        double total=route.Sum(r=>RouteGeometry.Length(r));
        string? failure=null;
        if(unresolved||confirmedUntil<total-.01)
        {
            double at=unresolved?Math.Min(confirmedUntil,unresolvedAlong):confirmedUntil;
            double offset=0;DirectedRoad? edge=null;
            foreach(var candidate in route)
            {
                double end=offset+RouteGeometry.Length(candidate);
                if(end>at+.001){edge=candidate;break;}offset=end;
            }
            failure=$"{edge?.Id}:{edge?.StartNodeUid:X16}:{edge?.EndNodeUid:X16}:{unresolved}";
            if(failure!=connectionFailureSignature)
                log?.Invoke($"导航连接解析错误：已加载的连续路线内存在未能确认的连接；对象={edge?.Id}，入口={edge?.StartNodeUid:X16}，出口={edge?.EndNodeUid:X16}，原因={(unresolved?"合流或分流结构未定":"原生几何或路口连接无法解析")}。");
        }
        connectionFailureSignature=failure;
    }

    private static string? CurrentJunction(NavigationStructurePlan plan,double along)
    {
        double offset=0;
        foreach(var edge in plan.Route)
        {
            double end=offset+RouteGeometry.Length(edge);
            if(along>offset && along<end)
            {
                if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal))return null;
                var parts=edge.Id.Split(':');
                return parts.Length==4 ? "prefab:"+parts[1] : null;
            }
            offset=end;
        }
        return null;
    }

    public NavigationDiagnostic Suspend(string state, DateTimeOffset now)
    {
        SuspendSources();
        if(guidance is null)Coordinator.ClearPreparation();
        Report(state, null, now);
        return new(state, null, lastSpoken);
    }

    private void SuspendSources()
    {
        Coordinator.Suspend("navigation");
        Coordinator.Suspend(CityEntryEventBuilder.Kind);
        Coordinator.Suspend(NarrowRoadEventBuilder.Kind);
    }

    private void Report(string state, NavigationManeuver? next, DateTimeOffset now)
    {
        string current = state + ":" + next?.Id;
        if (signature == current && now - reportedAt < TimeSpan.FromSeconds(30)) return;
        signature = current;
        reportedAt = now;
        log?.Invoke("导航状态：" + state + (next is null ? "" :
            FormattableString.Invariant($" 下一操作={next.Kind}，沿路={next.DistanceMeters:F0} m，路口={next.Id}")));
    }

    public void Reset()
    {
        ferryProgress.Reset();
        if(guidance is null)Coordinator.Reset();else SuspendSources();
        planningSession=null;eventPlanner.Reset();progress.Reset();narrowPlanner.Reset();cityPlanner.Reset();cityEntries.Reset();citySession=null;cityFrame=null;
        structures.Clear();publishedStructure=null;publishedGeneration=-1;mapIdentity=null;routeBuildGeneration=0;structureFirstNodeOrdinal=0;
        connectionFailureSignature=null;
        map = null;
        paths?.Dispose();paths = null;
        lastSpoken = signature = null;
    }
}
