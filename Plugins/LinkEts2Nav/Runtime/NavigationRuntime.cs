using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

public sealed record NavigationDiagnostic(string State, NavigationManeuver? Next = null, string? LastSpoken = null);

/// <summary>Ordinary junction instructions along the game's selected route; no lane guidance.</summary>
public sealed class NavigationRuntime(SpeechAnnouncementQueue? speech = null, Action? fallback = null,
    Action<string>? log = null)
{
    private readonly NavigationManeuverDetector detector = new();
    private readonly NavigationInstructionPolicy policy = new();
    private readonly DirectedPathMatcher matcher = new();
    private TruckLib.ScsMap.Map? map;
    private RoadPathSource? paths;
    private string? lastSpoken, signature;
    private DateTimeOffset reportedAt;

    public NavigationDiagnostic Process(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        var vehicle = data.Vehicle;
        if (!data.IsVehicleUsable || vehicle is not { SdkActive: true, Paused: false, IsFinite: true }
            || vehicle.SpeedMetersPerSecond < 0 || now < vehicle.ReceivedAt || now - vehicle.ReceivedAt > TimeSpan.FromSeconds(2))
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
        if (along is null) return Suspend("当前位置未唯一匹配连续导航道路，暂不提示转向。", now);
        var next = detector.FindNext(route, along.Value, paths.GetNavigationGraph());
        var plan = policy.Plan(next, data.SessionId, vehicle.ReceivedAt, now, true);
        speech?.UpdatePlan("navigation", plan, priority: 10);
        foreach (string text in policy.Update(next, data.SessionId, vehicle.ReceivedAt, now, true))
        {
            log?.Invoke("导航指令：" + text + " 路口=" + next!.Id);
            if (speech is null || !speech.TryAnnounce(text, "导航"))
            {
                log?.Invoke("导航语音未就绪，使用提示音且不延迟补播：" + text);
                fallback?.Invoke();
            }
            lastSpoken = text;
        }
        string state = next is null ? "当前连续路径内没有可确认的普通路口操作。" : "沿游戏导航路线提供基础路口提示。";
        Report(state, next, now);
        return new(state, next, lastSpoken);
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
        speech?.ClearPlan("navigation");
        map = null;
        paths = null;
        lastSpoken = signature = null;
    }
}
