using System.Numerics;
using ETS2LA.Logging;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

/// <summary>Logs transitions immediately and a heartbeat every 30 seconds, never every tick.</summary>
public sealed class NavigationLogReporter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly Action<string> info;
    private readonly Action<string> warning;
    private readonly Dictionary<string, string> changes = new();
    private (string Session, double Limit)? currentLimit;
    private readonly Dictionary<string, (string Decision, bool Verified, int? Limit, string? Reason)> cameras = new();
    private string? state;
    private DateTimeOffset stateAt = DateTimeOffset.MinValue;
    private string? scan;
    private DateTimeOffset scanAt = DateTimeOffset.MinValue;
    private DateTimeOffset scanStartedAt = DateTimeOffset.MinValue;

    public NavigationLogReporter(Action<string>? info = null, Action<string>? warning = null)
    {
        this.info = info ?? (message => Logger.Info(Escape(message)));
        this.warning = warning ?? (message => Logger.Warn(Escape(message)));
    }

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
    private static string Stamp(string message, DateTimeOffset now)
        => $"[LinkEts2Nav] [{now.UtcDateTime:HH:mm:ss} UTC] {message}";

    public void Event(string message, DateTimeOffset now) => info(Stamp(message, now));

    public void Change(string key, string message, DateTimeOffset now, bool isWarning = false)
    {
        string value = $"{isWarning}:{message}";
        if (changes.TryGetValue(key, out var previous) && previous == value) return;
        changes[key] = value;
        (isWarning ? warning : info)(Stamp(message, now));
    }

    public void Status(string message, DateTimeOffset now, bool isWarning = false, string? detail = null)
    {
        string value = $"{isWarning}:{message}";
        if (state == value && now - stateAt < Interval) return;
        state = value;
        stateAt = now;
        (isWarning ? warning : info)(Stamp("状态：" + message + (detail is null ? "" : "；" + detail), now));
    }

    public void ScanStarted(Vector3 position, DateTimeOffset now)
    {
        if (scanStartedAt != DateTimeOffset.MinValue && now - scanStartedAt < Interval) return;
        scanStartedAt = now;
        Event(FormattableString.Invariant($"开始扫描附近地图：X={position.X:F1}, Z={position.Z:F1}，半径 1100 m。"), now);
    }

    public void Scanned(NearbyMapSnapshot nearby, Vector3 position, TimeSpan elapsed, DateTimeOffset now)
    {
        string signature = string.Join(",", nearby.Sectors.Select(s => $"{s.X}/{s.Z}")) + ":"
            + string.Join(",", nearby.Cameras.Select(c => c.Id).Order(StringComparer.Ordinal)) + ":"
            + string.Join("|", nearby.Diagnostics);
        if (scan == signature && now - scanAt < Interval) return;
        scan = signature;
        scanAt = now;
        Event(FormattableString.Invariant($"扫描完成：{nearby.Sectors.Count} 个区块，{nearby.Map.MapItems.Count} 个地图条目，{nearby.PrefabPaths.Count} 条路口连接，附近 {nearby.Cameras.Count} 个测速挂点；耗时 {elapsed.TotalMilliseconds:F0} ms；X={position.X:F1}, Z={position.Z:F1}。"), now);
        if (nearby.Diagnostics.Count > 0)
            warning(Stamp($"扫描有 {nearby.Diagnostics.Count} 条资源诊断：{string.Join(" | ", nearby.Diagnostics.Take(3))}", now));
    }

    public void Report(NavDiagnosticSnapshot snapshot)
    {
        Status(snapshot.State + " 路线=" + snapshot.RouteState, snapshot.UpdatedAt,
            detail: $"车辆={snapshot.VehiclePosition ?? "未知"}，附近探头={snapshot.CameraCount}。");
        // Waiting/paused snapshots are not evidence that previously observed cameras disappeared.
        if (snapshot.VehiclePosition is null) return;
        var present = new HashSet<string>(snapshot.Cameras.Select(c => c.Id), StringComparer.Ordinal);
        foreach (string id in cameras.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            Event($"目标离开当前诊断列表：{id}（不代表已经过监测位置）。", snapshot.UpdatedAt);
            cameras.Remove(id);
        }
        foreach (var camera in snapshot.Cameras)
        {
            var current = (camera.Decision, camera.DirectionVerified, camera.SpeedLimitKph, camera.SpeedLimitReason);
            if (cameras.TryGetValue(camera.Id, out var previous) && previous == current) continue;
            string action = cameras.ContainsKey(camera.Id) ? "目标判定变化" : "发现探头";
            cameras[camera.Id] = current;
            string roadDistance = camera.RoadDistanceMeters is { } distance ? $"{distance:F0} m" : "未知";
            string limitText = camera.SpeedLimitKph is { } speed ? $"，限速估算={speed} km/h" : "，限速估算=未知";
            limitText += "（" + camera.SpeedLimitReason + "）";
            string routeBound = camera.RouteDistanceLowerBoundMeters is { } bound ? $"，已知路线长度下限={bound:F0} m" : "";
            Event(FormattableString.Invariant($"{action}：{camera.Id}，挂点={camera.Hookup}，直线={camera.DistanceMeters:F0} m，沿路={roadDistance}{routeBound}{limitText}，方向已验证={camera.DirectionVerified}；{camera.Decision}；模型={camera.SourceDescriptor}，变体={camera.Variant}。"), snapshot.UpdatedAt);
        }
    }

    public void ReportCurrentLimit(GameDataSnapshot data, DateTimeOffset now)
    {
        if (!data.CanPrepareMap(now) || data.Vehicle is not { CurrentSpeedLimitKph: { } speed } vehicle) return;
        var key = (data.SessionId, speed);
        if (currentLimit == key) return;
        currentLimit = key;
        Event(FormattableString.Invariant($"SDK 当前限速={speed:F1} km/h；X={vehicle.X:F1}, Y={vehicle.Y:F1}, Z={vehicle.Z:F1}；朝向={vehicle.HeadingTurns:F5} 圈；会话={data.SessionId}"), now);
    }

    public void ReportFines(IReadOnlyList<FineObservation> observations, long droppedCount, DateTimeOffset now)
    {
        foreach (var fine in observations)
        {
            string currentSpeedLimit = fine.Vehicle.CurrentSpeedLimitKph is { } speedLimit ? $"{speedLimit:F1} km/h" : "未知";
            string signal = fine.IsSignalCertain ? "罚款位翻转" : "仅字段变化，事件次数不确定";
            Event(FormattableString.Invariant($"观测罚单：{fine.Offence}，金额={fine.Amount}，X={fine.Vehicle.X:F1}, Y={fine.Vehicle.Y:F1}, Z={fine.Vehicle.Z:F1}；朝向={fine.Vehicle.HeadingTurns:F5} 圈；速度={fine.Vehicle.SpeedMetersPerSecond * 3.6:F1} km/h；当前位置限速={currentSpeedLimit}；序号={fine.Sequence}，标志={fine.EventFlag}，信号={signal}；观测时刻={fine.ObservedAt:O}；此位置可能有轮询延迟。"), now);
        }
        if (droppedCount > 0)
            Change("dropped-fines", $"罚款观察队列已溢出：累计 {droppedCount} 条未输出记录被丢弃，不能据此推断未发生罚款。", now, isWarning: true);
    }
    public void ResetTargets(string reason, DateTimeOffset now)
    {
        cameras.Clear();
        scan = null;
        scanStartedAt = DateTimeOffset.MinValue;
        Event(reason, now);
    }

    public void Triggered(CameraWarning alert, DateTimeOffset now)
        => Event(FormattableString.Invariant($"触发预警：{alert.CameraId}，阶段={alert.StageDistanceMeters:F0} m，距离={alert.DistanceMeters:F1} m，距离类型={alert.DistanceKind}，首次确认已不足200米={alert.FirstConfirmedInside200Meters}；提交预警音频播放请求。"), now);
}
