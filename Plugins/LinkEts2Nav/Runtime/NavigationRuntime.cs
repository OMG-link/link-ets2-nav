using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record NavigationDiagnostic(string State, NavigationManeuver? Next = null, string? LastSpoken = null)
{
    public IReadOnlyList<LaneMergeObservation> LaneMerges { get; init; } = [];
    public LaneNavigationPlan? LaneRoute { get; init; }
    public IReadOnlyList<NavigationManeuver> Upcoming { get; init; } = [];
    public double? ConfirmedAheadMeters { get; init; }
    public bool DeferredUntilJunctionExit { get; init; }
}

/// <summary>Ordinary junction instructions along the game's selected route; no lane guidance.</summary>
public sealed class NavigationRuntime(SpeechAnnouncementQueue? speech = null, Action? fallback = null,
    Action<string>? log = null)
{
    private readonly LaneMergeDetector mergeDetector = new();
    private readonly LaneRoutePlanner lanePlanner = new();
    private readonly NavigationManeuverDetector detector = new();
    private readonly NavigationInstructionPolicy policy = new();
    private readonly DirectedPathMatcher matcher = new();
    private TruckLib.ScsMap.Map? map;
    private RoadPathSource? paths;
    private string? lastSpoken, signature, mergeSignature;
    private string? approachingJunction;
    private bool approachingStraight;
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
            paths = new RoadPathSource(nearby.Map, nearby.PrefabPaths);
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
        var upcoming = detector.FindSequence(instructionRoute, along.Value, paths.GetNavigationGraph(), out double confirmedUntil, out bool hasUnresolvedJoin);
        var laneRoute=lanePlanner.Build(route,position,along.Value,paths.GetNavigationGraph());
        // Recognized roundabouts already own entry/exit guidance; their internal joins are not ramp actions.
        var merges=mergeDetector.Find(route,paths.GetNavigationGraph(),laneRoute,along.Value)
            .Where(m=>!nearby.RoadStructures.ContainsKey(m.PrefabUid)).ToArray();
        string mergeState=string.Join("|",merges.Select(m=>$"{m.Id}:{m.Kind}:{m.ParallelDistanceMeters}"));
        if(mergeState!=mergeSignature)
        {
            foreach(var merge in merges)log?.Invoke(FormattableString.Invariant($"车道合并判定：{merge.Kind}，距汇合点={merge.DistanceMeters:F0} m，平行接近长度={merge.ParallelDistanceMeters?.ToString("F0")??"未知"} m，向{(merge.MoveLeft?"左":"右")}合并；{merge.Id}；{merge.Reason}"));
            mergeSignature=mergeState;
        }
        foreach(var merge in merges)
        {
            string group=$"prefab:{merge.PrefabUid:X16}";
            upcoming=upcoming.Where(m=>m.JunctionId!=group||m.Kind is ManeuverKind.TurnLeft or ManeuverKind.TurnRight or ManeuverKind.UTurn or ManeuverKind.EnterRoundabout or ManeuverKind.ExitRoundabout or ManeuverKind.PassRoundaboutExit).ToArray();
            string side=merge.MoveLeft?"左":"右";
            string lanes=merge.RecommendedLaneCount==1?"车道":(merge.RecommendedLaneCount==2?"两":NavigationPrompts.Number(merge.RecommendedLaneCount))+"条车道";
            var action=new NavigationManeuver(merge.Id,merge.MoveLeft?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,merge.DistanceMeters,"lane","lane")
            { JunctionId="lane-merge:"+merge.PrefabUid.ToString("X16"),HasSeparateApproachRoad=false,
                AnnouncementMode=merge.Kind==LaneMergeKind.LaneReduction?NavigationAnnouncementMode.NoticeAt500:NavigationAnnouncementMode.Silent,
                Instruction=$"请注意前方车道合并，建议走{side}侧{lanes}" };
            upcoming=upcoming.Append(action).OrderBy(m=>m.DistanceMeters).ToArray();
        }
        if(yardApproach)
        {
            string group="prefab:"+route[0].Id.Split(':')[1];
            if(!upcoming.Any(m=>m.JunctionId==group && m.Kind!=ManeuverKind.Straight && m.DistanceMeters==0))
                return Suspend("货场出口路径已找到，但出口操作尚无法确认。",now);
            upcoming=upcoming.Select(m=>m with{DistanceMeters=m.DistanceMeters+exitDistance}).ToArray();
        }
        double confirmedAhead = Math.Max(0, confirmedUntil - along.Value);
        bool confirmedContinuation = !yardApproach && !hasUnresolvedJoin && !upcoming.Any(m => m.Kind != ManeuverKind.Straight) && confirmedAhead >= 800;
        var next = upcoming.FirstOrDefault(m => m.Kind != ManeuverKind.Straight) ?? upcoming.FirstOrDefault();
        string? currentJunction=yardApproach ? null : CurrentJunction(instructionRoute,along.Value);
        bool finishingTrackedTurn=currentJunction is not null && currentJunction==approachingJunction && !approachingStraight;
        if(!finishingTrackedTurn && upcoming.FirstOrDefault() is { DistanceMeters: <=50, JunctionId: not null } approach)
        {
            approachingJunction=approach.JunctionId;
            approachingStraight=approach.Kind==ManeuverKind.Straight;
        }
        // Entering a turn's PPD curve is not completion. Keep preparing future audio,
        // but defer the next briefing until exit; an imminent execution cue remains urgent.
        bool defer=currentJunction is not null && currentJunction==approachingJunction && !approachingStraight && currentJunction!=next?.JunctionId
            && !(next is { Kind: not ManeuverKind.Straight, DistanceMeters: <=20 });
        var plan = policy.PlanSequence(upcoming, data.SessionId, vehicle.ReceivedAt, now, true, confirmedContinuation);
        speech?.UpdatePlan("navigation", plan, priority: 10);
        foreach (string text in policy.UpdateSequence(upcoming, data.SessionId, vehicle.ReceivedAt, now, !defer,
            speech is null ? null : text => speech.IsReady(text) || speech.HasFailed(text), confirmedContinuation))
        {
            log?.Invoke("导航指令：" + text + " 当前路口=" + upcoming.FirstOrDefault()?.Id + "；下一动作=" + next?.Id);
            if (speech is null || !speech.TryAnnounce(text, "导航"))
            {
                log?.Invoke("导航语音未就绪，使用提示音且不延迟补播：" + text);
                fallback?.Invoke();
            }
            lastSpoken = text;
        }
        string state = yardApproach ? "货场出口方向提示（距出口采用直线距离）。" : defer ? "正在通过当前路口，驶出后继续提示下一动作。" : next is not null ? "沿游戏导航路线提供基础路口提示。"
            : confirmedContinuation ? "已确认前方长直路，沿当前道路行驶，等待下一操作提示。"
            : "当前连续路径内没有可确认的普通路口操作。";
        Report(state, next, now);
        return new(state, next, lastSpoken) { LaneRoute = laneRoute, LaneMerges = merges, Upcoming = upcoming, ConfirmedAheadMeters = confirmedAhead, DeferredUntilJunctionExit = defer };
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
        speech?.ClearPlan("navigation");
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
        policy.Reset();
        mergeDetector.Reset();
        mergeSignature=null;
        speech?.ClearPlan("navigation");
        map = null;
        paths = null;
        lastSpoken = signature = approachingJunction = null;
        approachingStraight=false;
    }
}
