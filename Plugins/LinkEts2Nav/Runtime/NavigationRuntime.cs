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
    private readonly NavigationStructurePlanner structurePlanner = new();
    private readonly LaneRoutePlanner lanePlanner = new();
    private readonly Dictionary<IReadOnlyList<DirectedRoad>,NavigationStructurePlan> structures = new(ReferenceEqualityComparer.Instance);
    private NavigationStructurePlan? publishedStructure;
    private NavigationMapIdentity? mapIdentity;
    private int routeBuildGeneration;
    private double routeOrigin;
    public int StructureBuildCount { get; private set; }
    public int EventPlanBuildCount { get; private set; }
    private readonly GuidanceTimeline analysisTimeline=new();
    private readonly SpeechTriggerScheduler analysisScheduler=new();
    private string? planningSession;
    private RouteSnapshot? analysisRoute;
    private readonly NavigationEventPlanner eventPlanner=new();
    private readonly RouteProgressTracker progress=new();
    private readonly EnvironmentPreviewPlanner cityPlanner=new();
    private readonly EnvironmentPreviewPlanner narrowPlanner=new();
    private readonly CityEntryEventBuilder cityEntries=new(log,spokenCityNames);
    private string? citySession;
    private string? cityFrame;
    private readonly DirectedPathMatcher matcher = new();
    private TruckLib.ScsMap.Map? map;
    private RoadPathSource? paths;
    private string? lastSpoken, signature;
    private string? connectionFailureSignature;
    private DateTimeOffset reportedAt;

    public NavigationDiagnostic Process(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        var vehicle = data.Vehicle;
        if (!data.IsVehicleUsable || vehicle is not { SdkActive: true, Paused: false, IsFinite: true }
            || vehicle.SpeedMetersPerSecond < -.1 || now < vehicle.ReceivedAt || now - vehicle.ReceivedAt > TimeSpan.FromSeconds(2))
            return Suspend("等待有效的行驶数据。", now);
        if (data.RouteAvailability != RouteAvailability.Available)
            return Suspend(data.RouteAvailability == RouteAvailability.None
                ? "未设置游戏导航路线，转向提示暂挂。" : "等待有效的游戏导航路线。", now);
        if (mapIdentity is null || !mapIdentity.Matches(nearby))
        {
            mapIdentity=new(nearby);map=nearby.Map;
            paths=new RoadPathSource(nearby.Map,nearby.PrefabPaths,log);
            structures.Clear();publishedStructure=null;routeBuildGeneration=0;
        }
        var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
        var route = paths!.GetPath(position, vehicle.ForwardDirection, data.Route, data.RouteAvailability);
        var along = matcher.ProjectDistance(position, vehicle.ForwardDirection, route);
        bool yardApproach=false;double exitDistance=0;
        if(along is null)
        {
            var areas=nearby.FreeNavigationAreas.Where(a=>a.Contains(position)).ToArray();
            var first=data.Route.Nodes.OrderByDescending(n=>n.DistanceToEndMeters).FirstOrDefault();
            if(areas.Length==1 && first is not null && areas[0].ExitNodes.Contains(first.Uid))
            {
                var prefix=paths.GetRoutePrefix(position,data.Route,first.Uid);
                if(prefix.Count>0 && prefix[0].Id.StartsWith("prefab:",StringComparison.Ordinal))
                {
                    var exit=prefix[0].Points[0];var delta=exit-position;
                    exitDistance=new Vector2(delta.X,delta.Z).Length();
                    if(exitDistance>0 && exitDistance<=100 && Math.Abs(delta.Y)<=3
                        && Vector3.Dot(Vector3.Normalize(new(delta.X,0,delta.Z)),vehicle.ForwardDirection)>=.5f)
                    { route=prefix;along=-exitDistance;yardApproach=true; }
                }
            }
        }
        if (along is null) return Suspend("当前位置未唯一匹配连续导航道路，暂不提示转向。", now);
        if(routeBuildGeneration!=paths!.RouteStructureBuildCount)
        {
            structures.Clear();publishedStructure=null;routeBuildGeneration=paths.RouteStructureBuildCount;
        }
        var laneRoute=lanePlanner.Build(route,position,along.Value,paths.GetNavigationGraph());
        if(!structures.TryGetValue(route,out var structure))
        {
            structure=structurePlanner.Build(route,data.Route,nearby,paths,laneRoute);
            structures.Add(route,structure);StructureBuildCount++;
            ReportConnectionFailure(structure.InstructionRoute,structure.ConfirmedUntil,structure.Unresolved,structure.UnresolvedAlong);
            foreach(var issue in structure.Forks)
                log?.Invoke($"道路分流未定：{issue.Prefab}，入口={issue.EntryNode:X16}，出口={issue.ExitNode:X16}；{issue.Reason}");
        }
        else if(NavigationStructurePlanner.SelectionChanged(structure.Lanes,laneRoute))
        {
            structure=structurePlanner.SelectLanes(structure,laneRoute,nearby,paths);structures[route]=structure;
        }
        bool structuralUpdate=!ReferenceEquals(publishedStructure,structure)||planningSession!=data.SessionId;
        if(structuralUpdate)routeOrigin=progress.Update(data.SessionId,route,0);
        double playerAlong=routeOrigin+along.Value;
        var instructionRoute=structure.InstructionRoute;
        var upcoming=structure.Actions.Select(m=>Relative(m,along.Value)).Where(m=>m.DistanceMeters>=0).ToArray();
        var merges=structure.Merges.Select(m=>m with{DistanceMeters=m.DistanceMeters-along.Value,
            EndDistanceMeters=m.EndDistanceMeters-along.Value}).Where(m=>m.DistanceMeters>=0).ToArray();
        bool confirmedContinuation=!yardApproach&&!upcoming.Any(NavigationEventPlanner.IsNavigationAction);
        double confirmedAhead=Math.Max(0,Math.Min(structure.ConfirmedUntil,structure.UnresolvedAlong)-along.Value);
        var next=upcoming.FirstOrDefault(NavigationEventPlanner.IsNavigationAction);
        var currentJunction=yardApproach?null:CurrentJunction(instructionRoute,along.Value);
        double? componentEnd=structure.Actions.Where(NavigationEventPlanner.IsNavigationAction)
            .Where(m=>m.DistanceMeters<along&&m.EndDistanceMeters>along).Select(m=>m.EndDistanceMeters).FirstOrDefault();
        bool defer=componentEnd is not null&&currentJunction is not null&&next is not null&&currentJunction!=next.JunctionId;
        if(guidance is not null)
        {
            if(citySession!=data.SessionId){cityEntries.Reset();citySession=data.SessionId;}
            if(cityFrame!=progress.Frame)
            {
                guidance.ClearSource(CityEntryEventBuilder.Kind);cityPlanner.Reset();guidance.ClearSource(NarrowRoadEventBuilder.Kind);
                narrowPlanner.Reset();cityEntries.Rebase();cityFrame=progress.Frame;
            }
            if(structuralUpdate)
            {
                // Only newly discovered structure can create/re-anchor speech points.
                // Ordinary progress never calls any event planner or geometry recognizer.
                double? initialEnd=componentEnd is {} end?routeOrigin+end:null;
                bool tail=!yardApproach&&(structure.Actions.LastOrDefault(NavigationEventPlanner.IsNavigationAction)?.EndDistanceMeters??0)<Math.Min(structure.ConfirmedUntil,structure.UnresolvedAlong);
                guidance.Publish(eventPlanner.Build(data.SessionId,data.Route.Revision,vehicle.ReceivedAt,
                    playerAlong,upcoming,true,false,tail,guidance.Scheduler,progress.Frame,initialEnd,
                    guidance.WillReplaceSource("navigation",data.Route.Revision),routeOrigin+Math.Min(structure.ConfirmedUntil,structure.UnresolvedAlong)));
                var entries=cityEntries.Build(route,playerAlong,nearby.SpeedLimits,routeOrigin,nearby.PrefabPaths)
                    .Select(e=>e with{Id=e.Id+"#"+progress.Frame}).ToArray();
                if(guidance.WillReplaceSource(CityEntryEventBuilder.Kind,data.Route.Revision))
                    cityPlanner.Reconcile(entries.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,guidance.Scheduler,CityEntryEventBuilder.Kind);
                var city=cityPlanner.Build(CityEntryEventBuilder.Kind,playerAlong,entries,guidance.Scheduler,true);
                guidance.Publish(new(CityEntryEventBuilder.Kind,data.SessionId,data.Route.Revision,playerAlong,true,vehicle.ReceivedAt,city.Sequences){CoordinateFrame=progress.Frame});
                var narrow=NarrowRoadEventBuilder.Build(route,nearby.SpeedLimits,routeOrigin)
                    .Select(e=>e with{Id=e.Id+"#"+progress.Frame}).ToArray();
                if(guidance.WillReplaceSource(NarrowRoadEventBuilder.Kind,data.Route.Revision))
                    narrowPlanner.Reconcile(narrow.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,guidance.Scheduler,NarrowRoadEventBuilder.Kind);
                var narrowPlan=narrowPlanner.Build(NarrowRoadEventBuilder.Kind,playerAlong,narrow,guidance.Scheduler,true);
                guidance.Publish(new(NarrowRoadEventBuilder.Kind,data.SessionId,data.Route.Revision,playerAlong,true,vehicle.ReceivedAt,narrowPlan.Sequences){CoordinateFrame=progress.Frame});
                publishedStructure=structure;EventPlanBuildCount++;
            }
            cityEntries.Advance(playerAlong);
            foreach(string source in new[]{"navigation",CityEntryEventBuilder.Kind,NarrowRoadEventBuilder.Kind})
                guidance.UpdateProgress(source,data.SessionId,playerAlong,vehicle.ReceivedAt,true);
            lastSpoken=guidance.LastPlayed("navigation");
        }
        else
        {
            if(structuralUpdate)
            {
                double? initialEnd=componentEnd is {} end?routeOrigin+end:null;
                bool tail=!yardApproach&&(structure.Actions.LastOrDefault(NavigationEventPlanner.IsNavigationAction)?.EndDistanceMeters??0)<Math.Min(structure.ConfirmedUntil,structure.UnresolvedAlong);
                bool replaced=analysisRoute is {} previous
                    &&!RouteStructureIdentity.Nodes(previous).SequenceEqual(RouteStructureIdentity.Nodes(data.Route))
                    &&!RouteContinuity.IsForwardExtension(previous,data.Route);
                var plan=eventPlanner.Build(data.SessionId,data.Route.Revision,vehicle.ReceivedAt,
                    playerAlong,upcoming,true,false,tail,analysisScheduler,progress.Frame,initialEnd,
                    replaceRoute:replaced,confirmedContinuationEndAlong:routeOrigin+Math.Min(structure.ConfirmedUntil,structure.UnresolvedAlong));
                if(replaced)
                {
                    var retained=plan.Events.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
                    var old=analysisTimeline.Sources.FirstOrDefault(p=>p.Source=="navigation");
                    if(old is not null)analysisScheduler.ForgetEvents("navigation",old.Events.Where(e=>!retained.Contains(e.Id)&&e.AlongMeters>old.PlayerAlongMeters).Select(e=>e.Id));
                    analysisTimeline.Clear("navigation");
                }
                analysisTimeline.Publish(plan);analysisRoute=data.Route;
                EventPlanBuildCount++;
            }
            analysisTimeline.UpdateProgress("navigation",data.SessionId,playerAlong,vehicle.ReceivedAt,true);
            speech?.UpdatePlan("navigation",analysisScheduler.Prepare(analysisTimeline,now),priority:10);
            // This read-only adapter owns no player. Unready audio stays pending
            // in the same trigger ledger instead of becoming a diagnostic beep.
            if(speech is not null)
            {
                var pending=analysisTimeline.Sources.Single(p=>p.Source=="navigation");
                analysisTimeline.Publish(pending with{Sequences=pending.Sequences.Select(sequence=>sequence.MapTriggers(t=>t with
                {Enabled=t.Silent||t.SkipAtSpeedKph is {} threshold&&vehicle.SpeedMetersPerSecond*3.6>=threshold
                    ||speech.IsReady(t.Text)||speech.HasFailed(t.Text)})).ToArray()});
            }
            foreach(var due in analysisScheduler.Update(analysisTimeline,now,vehicle.SpeedMetersPerSecond)
                .Where(r=>r.Disposition==TriggerDisposition.Queued).OrderBy(r=>r.Trigger.Priority))
            {
                string text=due.Trigger.Text;
                log?.Invoke("导航指令："+text+" 当前路口="+upcoming.FirstOrDefault()?.Id+"；下一动作="+next?.Id);
                lastSpoken=text;
            }
        }
        publishedStructure=structure;planningSession=data.SessionId;
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

    private static double? CurrentComponentStart(IReadOnlyList<DirectedRoad> route,double along)
    {
        double offset=0;
        foreach(var edge in route)
        {
            double end=offset+RouteGeometry.Length(edge);
            if(along>=offset&&along<end)
                return edge.Roundabout is not null||edge.Id.StartsWith("prefab:",StringComparison.Ordinal)?offset:null;
            offset=end;
        }
        return null;
    }

    private static string? CurrentJunction(IReadOnlyList<DirectedRoad> route,double along)
    {
        double offset=0;
        foreach(var edge in route)
        {
            double end=offset+RouteGeometry.Length(edge);
            if(along>offset && along<end)
            {
                if(edge.Roundabout is {} ring)return "roundabout:"+ring.Id;
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
        speech?.ClearPlan("navigation");guidance?.Suspend("navigation");guidance?.Suspend(CityEntryEventBuilder.Kind);guidance?.Suspend(NarrowRoadEventBuilder.Kind);
        Report(state, null, now);
        return new(state, null, lastSpoken);
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
        analysisTimeline.Reset();analysisScheduler.Reset();analysisRoute=null;planningSession=null;eventPlanner.Reset();progress.Reset();narrowPlanner.Reset();cityPlanner.Reset();cityEntries.Reset();citySession=null;cityFrame=null;guidance?.Suspend("navigation");guidance?.Suspend(CityEntryEventBuilder.Kind);guidance?.Suspend(NarrowRoadEventBuilder.Kind);
        structures.Clear();publishedStructure=null;mapIdentity=null;routeBuildGeneration=0;
        connectionFailureSignature=null;
        speech?.ClearPlan("navigation");
        map = null;
        paths = null;
        lastSpoken = signature = null;
    }
}
