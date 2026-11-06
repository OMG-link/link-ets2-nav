using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Runtime;

/// <summary>Connects immutable game samples to geometry matching and the pure warning policy.</summary>
public sealed class WarningPipeline(IWarningAnnouncer announcer)
{
    private readonly CameraWarningEngine engine = new();
    private TruckLib.ScsMap.Map? previousMap;
    private RoadPathSource? paths;
    private string? lastWarning;

    public NavDiagnosticSnapshot Process(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        if (!data.IsVehicleUsable || data.Vehicle is not { } vehicle)
            return new(now, "等待有效的车辆数据；暂停或连接中断期间不预警。", data.RouteAvailability.ToString(), nearby.Cameras.Count, [], lastWarning, LatestFine: data.LatestFine);
        var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
        if (!ReferenceEquals(previousMap, nearby.Map))
        {
            previousMap = nearby.Map;
            paths = new RoadPathSource(nearby.Map);
        }
        bool hasRoute = data.RouteAvailability == RouteAvailability.Available;
        bool usableRoute = hasRoute || data.RouteAvailability == RouteAvailability.None;
        var candidates = new List<CameraCandidate>();
        var diagnostics = new List<CameraDiagnostic>();
        foreach (var camera in nearby.Cameras)
        {
            double directDistance = Vector3.Distance(position, camera.MonitoredPosition);
            var candidate = new CameraCandidate(camera.Id, CameraApproach.Unknown, directDistance);
            string decision;
            if (!usableRoute) decision = "路线数据缺失或过期";
            else if (!camera.DirectionVerified) decision = "监测位置和方向尚未校准";
            else
            {
                candidate = paths!.MatchCamera(position, vehicle.ForwardDirection,
                    new(camera.Id, camera.MonitoredPosition, camera.TravelDirection), data.Route, data.RouteAvailability);
                decision = candidate.Approach switch
                {
                    CameraApproach.RouteAhead => candidate.RoadDistanceMeters.HasValue ? "位于导航前方" : "位于导航前方（直线距离回退）",
                    CameraApproach.UnavoidableAhead => "当前道路必经",
                    CameraApproach.Behind => "已在身后",
                    _ => "方向或位置匹配不确定"
                };
            }
            candidates.Add(candidate);
            diagnostics.Add(new(camera.Id, camera.Hookup, directDistance, camera.DirectionVerified, decision,
                candidate.RoadDistanceMeters, camera.SourceDescriptor, camera.ModelVariant, camera.MetadataFingerprint));
        }
        // Raw pose age, not processing time, controls whether alerts may fire.
        var warnings = engine.Update(new(data.SessionId, data.Route.Revision, hasRoute,
            usableRoute && vehicle.SpeedMetersPerSecond >= 0, vehicle.ReceivedAt, candidates), now);
        foreach (var warning in warnings)
        {
            announcer.Announce(warning);
            lastWarning = $"{warning.CameraId} · {warning.DistanceMeters:F0} m · {warning.DistanceKind}";
        }
        string state = nearby.Cameras.Any(c => c.DirectionVerified)
            ? "预警运行中；只有位置和方向已验证的目标会播报。"
            : "附近探头识别运行中；监测位置和方向未经校准，自动播报尚未启用。";
        if (nearby.Diagnostics.Count > 0) state += " " + nearby.Diagnostics[0];
        return new(now, state, data.RouteAvailability.ToString(), nearby.Cameras.Count,
            diagnostics.OrderBy(d => d.DistanceMeters).Take(30).ToArray(), lastWarning,
            FormattableString.Invariant($"{vehicle.X:F1}, {vehicle.Y:F1}, {vehicle.Z:F1}"), data.LatestFine);
    }

    public void Reset()
    {
        engine.Reset();
        previousMap = null;
        paths = null;
        lastWarning = null;
    }
}
