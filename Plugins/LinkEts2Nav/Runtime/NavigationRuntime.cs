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
    Action<string>? log = null,GuidanceCoordinator? guidance=null)
{
    private readonly LaneMergeDetector mergeDetector = new();
    private readonly LaneExitApproach exitApproach = new();
    private readonly AccelerationLaneApproach acceleration = new();
    private readonly LaneRoutePlanner lanePlanner = new();
    private readonly NavigationManeuverDetector detector = new();
    private readonly NavigationInstructionPolicy policy = new();
    private readonly NavigationEventPlanner eventPlanner=new();
    private readonly RouteProgressTracker progress=new();
    private readonly EnvironmentPreviewPlanner cityPlanner=new();
    private readonly CityEntryEventBuilder cityEntries=new(log);
    private string? citySession;
    private string? cityFrame;
    private readonly DirectedPathMatcher matcher = new();
    private TruckLib.ScsMap.Map? map;
    private RoadPathSource? paths;
    private string? lastSpoken, signature, mergeSignature;
    private string? forkSignature, connectionFailureSignature;
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
        if (!ReferenceEquals(map, nearby.Map))
        {
            map = nearby.Map;
            paths = new RoadPathSource(nearby.Map, nearby.PrefabPaths,log);
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
                    { route=prefix;along=0;yardApproach=true; }
                }
            }
        }
        if (along is null) return Suspend("当前位置未唯一匹配连续导航道路，暂不提示转向。", now);
        var instructionRoute=RoundaboutRouteAdapter.Apply(route,nearby.RoadStructures);
        // Classify the current component from its entrance too. Starting the plugin
        // inside a transparent straight must not invent a preceding action boundary.
        double detectionAlong=CurrentComponentStart(instructionRoute,along.Value)??along.Value;
        var detected=detector.FindSequence(instructionRoute,detectionAlong,paths.GetNavigationGraph(),out double confirmedUntil,out bool unresolvedJoin,out double firstUnresolvedJoinAlong);
        double shift=detectionAlong-along.Value;
        double? initialActionEnd=detected.Where(NavigationEventPlanner.IsNavigationAction)
            .Where(m=>m.DistanceMeters+shift<0&&m.EndDistanceMeters+shift>0)
            .Select(m=>m.EndDistanceMeters+shift).FirstOrDefault();
        IReadOnlyList<NavigationManeuver> upcoming=detected.Select(m=>m with{DistanceMeters=m.DistanceMeters+shift,
            EndDistanceMeters=m.EndDistanceMeters+shift,SpeechSequenceStartDistanceMeters=m.SpeechSequenceStartDistanceMeters+shift})
            .Where(m=>m.DistanceMeters>=0).ToArray();
        ReportConnectionFailure(instructionRoute, confirmedUntil, unresolvedJoin, firstUnresolvedJoinAlong);
        string forkState=string.Join("|",detector.ForkDiagnostics.Select(f=>$"{f.Prefab}:{f.EntryNode}:{f.ExitNode}:{f.Reason}"));
        if(forkState!=forkSignature)
        {
            foreach(var issue in detector.ForkDiagnostics)
                log?.Invoke($"道路分流未定：{issue.Prefab}，入口={issue.EntryNode:X16}，出口={issue.ExitNode:X16}；{issue.Reason}");
            forkSignature=forkState;
        }
        var laneRoute=lanePlanner.Build(route,position,along.Value,paths.GetNavigationGraph());
        // Recognized roundabouts already own entry/exit guidance; their internal joins are not ramp actions.
        var merges=mergeDetector.Find(route,paths.GetNavigationGraph(),laneRoute,along.Value)
            .Where(m=>!nearby.RoadStructures.ContainsKey(m.PrefabUid)).ToArray();
        var accelerationActions=acceleration.Find(instructionRoute,paths.GetNavigationGraph(),along.Value);
        NavigationManeuver? ProvenAcceleration(LaneMergeObservation m)=>accelerationActions.FirstOrDefault(a=>
            a.JunctionId=="prefab:"+m.PrefabUid.ToString("X16")&&m.MoveLeft is {} left
            &&a.Kind==(left?ManeuverKind.MergeLeft:ManeuverKind.MergeRight)&&a.EndDistanceMeters is {} end&&Math.Abs(end-m.DistanceMeters)<2);
        merges=merges.Select(m=>ProvenAcceleration(m) is {} proof?m with{Kind=LaneMergeKind.Ramp,
            ParallelDistanceMeters=proof.ApproachInterval!.LengthMeters,Reason="已沿原生车道继承证明独立入口、并行及最终收口完整区间"}:m).ToArray();
        string mergeState=string.Join("|",merges.Select(m=>$"{m.Id}:{m.Kind}:{m.ParallelDistanceMeters}:{m.MoveLeft}"));
        if(mergeState!=mergeSignature)
        {
            foreach(var merge in merges)log?.Invoke(FormattableString.Invariant($"车道合并判定：{merge.Kind}，距汇合点={merge.DistanceMeters:F0} m，平行接近长度={merge.ParallelDistanceMeters?.ToString("F0")??"未知"} m，{(merge.MoveLeft is null?"合并方向未知":"向"+(merge.MoveLeft==true?"左":"右")+"合并")}；{merge.Id}；{merge.Reason}"));
            mergeSignature=mergeState;
        }
        foreach(var merge in merges)
        {
            string group=$"prefab:{merge.PrefabUid:X16}";
            var accelerationProof=ProvenAcceleration(merge);
            bool replacesNavigation=accelerationProof is not null||merge.Kind==LaneMergeKind.Ramp&&merge.MoveLeft is not null;
            if(replacesNavigation)
                upcoming=upcoming.Where(m=>m.JunctionId!=group||m.Kind is ManeuverKind.TurnLeft or ManeuverKind.TurnRight or ManeuverKind.UTurn or ManeuverKind.EnterRoundabout or ManeuverKind.ExitRoundabout or ManeuverKind.PassRoundaboutExit).ToArray();
            if(accelerationProof is not null)continue;
            if(merge.MoveLeft is null)continue;
            string side=merge.MoveLeft==true?"左":"右";
            string lanes=merge.RecommendedLaneCount==1?"车道":(merge.RecommendedLaneCount==2?"两":NavigationPrompts.Number(merge.RecommendedLaneCount))+"条车道";
            double anchor=merge.Kind==LaneMergeKind.Ramp&&merge.ParallelDistanceMeters is {} length?new LaneActionInterval(merge.DistanceMeters-length,merge.DistanceMeters).AtFraction(.1):merge.DistanceMeters;
            var action=new NavigationManeuver(merge.Id,merge.MoveLeft==true?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,anchor,"lane","lane")
            { EndDistanceMeters=merge.EndDistanceMeters,JunctionKind=NavigationJunctionKind.Ramp,JunctionId="prefab:"+merge.PrefabUid.ToString("X16"),HasSeparateApproachRoad=false,
                AnnouncementMode=merge.Kind==LaneMergeKind.LaneReduction?NavigationAnnouncementMode.DistancePreview:merge.Kind==LaneMergeKind.Ramp?NavigationAnnouncementMode.Normal:NavigationAnnouncementMode.Silent,
                ApproachInterval=merge.Kind==LaneMergeKind.Ramp&&merge.ParallelDistanceMeters is {} span?new(merge.DistanceMeters-span,merge.DistanceMeters):null,
                Instruction=merge.Kind==LaneMergeKind.LaneReduction?$"车道合并，建议提前走{side}侧{lanes}":null };
            upcoming=upcoming.Append(action).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        upcoming=upcoming.Where(m=>!accelerationActions.Any(a=>m.JunctionId==a.JunctionId))
            .Concat(accelerationActions).OrderBy(m=>m.DistanceMeters).ToArray();
        if(NavigationDestination.Find(data.Route,instructionRoute,along.Value,confirmedUntil,unresolvedJoin) is {} destination)
            upcoming=upcoming.Append(destination).OrderBy(m=>m.DistanceMeters).ToArray();
        if(yardApproach)
        {
            string group="prefab:"+route[0].Id.Split(':')[1];
            if(!upcoming.Any(m=>m.JunctionId==group && m.Kind!=ManeuverKind.Straight && m.DistanceMeters==0))
                return Suspend("货场出口路径已找到，但出口操作尚无法确认。",now);
            upcoming=upcoming.Select(m=>m with{DistanceMeters=m.DistanceMeters+exitDistance,EndDistanceMeters=m.EndDistanceMeters+exitDistance,
                SpeechSequenceStartDistanceMeters=m.SpeechSequenceStartDistanceMeters+exitDistance}).ToArray();
        }
        // A distant unresolved join limits the safe stretch; it does not invalidate
        // an already verified long road before that join.
        upcoming = exitApproach.Apply(upcoming, instructionRoute, paths.GetNavigationGraph(), along.Value);
        double confirmedAhead = Math.Max(0, Math.Min(confirmedUntil, firstUnresolvedJoinAlong) - along.Value);
        bool confirmedContinuation = !yardApproach && !upcoming.Any(NavigationEventPlanner.IsNavigationAction);
        var next = upcoming.FirstOrDefault(NavigationEventPlanner.IsNavigationAction);
        string? currentJunction=yardApproach ? null : CurrentJunction(instructionRoute,along.Value);
        // This is a diagnostic only. Both execution paths use the planner's
        // same physical-end boundary instead of a separate turn/straight gate.
        bool defer=initialActionEnd is not null&&currentJunction is not null&&next is not null&&currentJunction!=next.JunctionId;
        double? componentEnd=initialActionEnd is {} remaining?along.Value+remaining:null;
        if(guidance is not null)
        {
            double playerAlong=progress.Update(data.SessionId,route,along.Value);
            guidance.Publish(eventPlanner.Build(data.SessionId,data.Route.Revision,vehicle.ReceivedAt,
                playerAlong,upcoming,true,false,confirmedContinuation,guidance.Scheduler,progress.Frame,
                componentEnd is {} boundary?playerAlong-along.Value+boundary:null,
                guidance.WillReplaceSource("navigation",data.Route.Revision),playerAlong+confirmedAhead));
            if(citySession!=data.SessionId){cityEntries.Reset();citySession=data.SessionId;}
            if(cityFrame!=progress.Frame){guidance.ClearSource(CityEntryEventBuilder.Kind);cityPlanner.Reset();cityEntries.Rebase();cityFrame=progress.Frame;}
            var entries=cityEntries.Build(route,playerAlong,nearby.SpeedLimits,playerAlong-along.Value,nearby.PrefabPaths)
                .Select(e=>e with{Id=e.Id+"#"+progress.Frame}).ToArray();
            if(guidance.WillReplaceSource(CityEntryEventBuilder.Kind,data.Route.Revision))
                cityPlanner.Reconcile(entries.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal),playerAlong,guidance.Scheduler,CityEntryEventBuilder.Kind);
            var city=cityPlanner.Build(CityEntryEventBuilder.Kind,playerAlong,entries,guidance.Scheduler,true);
            guidance.Publish(new(CityEntryEventBuilder.Kind,data.SessionId,data.Route.Revision,playerAlong,true,vehicle.ReceivedAt,city.Sequences){CoordinateFrame=progress.Frame});
            lastSpoken=guidance.LastPlayed("navigation");
        }
        else
        {
        var plan = policy.PlanSequence(upcoming, data.SessionId, vehicle.ReceivedAt, now, true, confirmedContinuation,componentEnd-along.Value,confirmedAhead);
        speech?.UpdatePlan("navigation", plan, priority: 10);
        foreach (string text in policy.UpdateSequence(upcoming, data.SessionId, vehicle.ReceivedAt, now, true,
            speech is null ? null : text => speech.IsReady(text) || speech.HasFailed(text), confirmedContinuation,vehicle.SpeedMetersPerSecond,componentEnd-along.Value,confirmedAhead))
        {
            log?.Invoke("导航指令：" + text + " 当前路口=" + upcoming.FirstOrDefault()?.Id + "；下一动作=" + next?.Id);
            lastSpoken = text;
        }
        }
        string state = yardApproach ? "货场出口方向提示（距出口采用直线距离）。" : defer ? "正在通过当前路口。" : next is not null ? "沿游戏导航路线提供基础路口提示。"
            : confirmedContinuation ? "当前可见路线没有非直行动作，沿当前道路行驶，等待下一操作提示。"
            : "当前连续路径内没有可确认的普通路口操作。";
        Report(state, next, now);
        return new(state, next, lastSpoken) { LaneRoute = laneRoute, LaneMerges = merges, RoadForks=detector.ForkDiagnostics, Upcoming = upcoming, ConfirmedAheadMeters = confirmedAhead, DeferredUntilJunctionExit = defer };
    }

    // Missing map tails are normal. A failure inside the already constructed
    // continuous graph is different: retain the physical edge identities for replay diagnosis.
    private void ReportConnectionFailure(IReadOnlyList<DirectedRoad> route,double confirmedUntil,
        bool unresolved,double unresolvedAlong)
    {
        double total=route.Sum(r=>r.Points.Zip(r.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second)));
        string? failure=null;
        if(unresolved||confirmedUntil<total-.01)
        {
            double at=unresolved?Math.Min(confirmedUntil,unresolvedAlong):confirmedUntil;
            double offset=0;DirectedRoad? edge=null;
            foreach(var candidate in route)
            {
                double end=offset+candidate.Points.Zip(candidate.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
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
            double end=offset+edge.Points.Zip(edge.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
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
            double end=offset+edge.Points.Zip(edge.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
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
        speech?.ClearPlan("navigation");guidance?.Suspend("navigation");guidance?.Suspend(CityEntryEventBuilder.Kind);
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
        policy.Reset();eventPlanner.Reset();progress.Reset();cityPlanner.Reset();cityEntries.Reset();citySession=null;cityFrame=null;guidance?.Suspend("navigation");guidance?.Suspend(CityEntryEventBuilder.Kind);
        mergeDetector.Reset(); exitApproach.Reset();acceleration.Reset();
        mergeSignature=forkSignature=connectionFailureSignature=null;
        speech?.ClearPlan("navigation");
        map = null;
        paths = null;
        lastSpoken = signature = null;
    }
}
