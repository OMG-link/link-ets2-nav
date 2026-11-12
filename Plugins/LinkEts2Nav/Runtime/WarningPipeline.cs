using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Runtime;

/// <summary>Connects immutable game samples to geometry matching and the pure warning policy.</summary>
public sealed class WarningPipeline(IWarningPlanner? planner = null,GuidanceCoordinator? guidance=null)
{
    private readonly CameraWarningEngine engine = new();
    private readonly LinkEts2Nav.Core.Guidance.RouteProgressTracker progress=new();
    private readonly DirectedPathMatcher matcher=new();
    private readonly CameraWarningEngine uncalibrated = new(kind: CameraWarningKind.UncalibratedProximity);
    private TruckLib.ScsMap.Map? previousMap;
    private RoadPathSource? paths;
    private SpeedLimitResolver? speedLimits;
    private string? lastWarning;

    public NavDiagnosticSnapshot Process(GameDataSnapshot data, NearbyMapSnapshot nearby, DateTimeOffset now)
    {
        if (!data.IsVehicleUsable || data.Vehicle is not { } vehicle)
        {
            planner?.ClearPlan();guidance?.Suspend("landmarks");
            return new(now, "等待有效的车辆数据；暂停或连接中断期间不预警。", data.RouteAvailability.ToString(), nearby.Cameras.Count, [], lastWarning, LatestFine: data.LatestFine, LatestCameraFine: data.LatestCameraFine,
                FineHistory: data.FineHistory, DroppedFineCount: data.DroppedFineCount);
        }
        var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
        if (!ReferenceEquals(previousMap, nearby.Map))
        {
            previousMap = nearby.Map;
            paths = new RoadPathSource(nearby.Map, nearby.PrefabPaths);
            speedLimits = new SpeedLimitResolver(nearby.SpeedLimits, paths);
        }
        bool hasRoute = data.RouteAvailability == RouteAvailability.Available;
        bool usableRoute = hasRoute || data.RouteAvailability == RouteAvailability.None;
        var candidates = new List<CameraCandidate>();
        var unknownCameras = new List<CameraCandidate>();
        var diagnostics = new List<CameraDiagnostic>();
        foreach (var camera in nearby.Cameras)
        {
            double directDistance = Vector3.Distance(position, camera.MonitoredPosition);
            var candidate = new CameraCandidate(camera.Id, CameraApproach.Unknown, directDistance);
            if (!camera.DirectionVerified && camera.ActivePartVerified)
                unknownCameras.Add(new(camera.Id, CameraApproach.UncalibratedNearby,
                    Vector3.Distance(position, camera.LocatorPosition)));
            string decision;
            if (!usableRoute) decision = $"路线数据缺失或过期（SDK节点={data.Route.Nodes.Count}，遥测剩余={vehicle.NavigationDistanceRemainingMeters:F0} m）";
            else if (!camera.DirectionVerified) decision = "监测位置和方向尚未校准";
            else
            {
                candidate = paths!.MatchCamera(position, vehicle.ForwardDirection,
                    new(camera.Id, camera.MonitoredPosition, camera.TravelDirection), data.Route, data.RouteAvailability);
                decision = candidate.Approach switch
                {
                    CameraApproach.RouteAhead => candidate.RoadDistanceMeters.HasValue ? "位于导航前方" :
                        "位于导航前方（直线距离回退，受已知道路长度下限约束）",
                    CameraApproach.UnavoidableAhead => "当前道路必经",
                    CameraApproach.Behind => "已在身后",
                    _ => candidate.MatchFailure ?? "方向或位置匹配不确定"
                };
            }
            var limit = camera.DirectionVerified ? speedLimits!.ResolveToCamera(data,new(camera.Id, camera.MonitoredPosition, camera.TravelDirection))
                : new SpeedLimitResult(null, "探头方向未校准");
            candidate = candidate with { SpeedLimitKph = limit.Kph };
            candidates.Add(candidate);
            diagnostics.Add(new(camera.Id, camera.Hookup, directDistance, camera.DirectionVerified, decision,
                candidate.RoadDistanceMeters, camera.SourceDescriptor, camera.ModelVariant, camera.MetadataFingerprint, candidate.RouteDistanceLowerBoundMeters, limit.Kph, limit.Reason));
        }
        // Raw pose age, not processing time, controls whether alerts may fire.
        var warningSnapshot = new WarningSnapshot(data.SessionId, data.Route.Revision, hasRoute,
            usableRoute && vehicle.SpeedMetersPerSecond >= 0, vehicle.ReceivedAt, candidates);
        var proximitySnapshot = new WarningSnapshot(data.SessionId, data.Route.Revision, false,
            vehicle.SpeedMetersPerSecond >= 0, vehicle.ReceivedAt, unknownCameras);
        // Proximity notices do not assert route membership or a measured traffic direction.
        var plans = engine.GetPlans(warningSnapshot, now)
            .Concat(uncalibrated.GetPlans(proximitySnapshot, now)).ToArray();
        if(guidance is not null)
        {
            var path=paths!.GetPath(position,vehicle.ForwardDirection,data.Route,data.RouteAvailability);
            var local=matcher.ProjectDistance(position,vehicle.ForwardDirection,path);
            double? playerAlong=local is {} value?progress.Update(data.SessionId,path,value):null;
            guidance.PublishLandmarks(proximitySnapshot,plans,engine.PassedEncounters.Concat(uncalibrated.PassedEncounters).ToArray(),
                playerAlong,playerAlong is null?null:"camera:"+progress.Frame,nearby.Cameras);
            lastWarning=guidance.LastQueued("landmarks")??lastWarning;
        }
        else
        {
        planner?.UpdatePlan(proximitySnapshot, plans, now);
        var warnings = engine.Update(warningSnapshot, now).Concat(uncalibrated.Update(proximitySnapshot, now));
        foreach (var warning in warnings)
        {
            lastWarning = $"{warning.CameraId} · {warning.StageDistanceMeters:F0} m 阶段 · 实距 {warning.DistanceMeters:F0} m · {warning.DistanceKind}";
        }
        }
        string state = nearby.Cameras.Any(c => c.DirectionVerified)
            ? "预警运行中；已校准目标按路线预警，未校准目标仅作500米接近提醒。"
            : "附近探头识别运行中；未校准目标仅作500米接近提醒。";
        if (nearby.Diagnostics.Count > 0) state += " " + nearby.Diagnostics[0];
        return new(now, state, data.RouteAvailability.ToString(), nearby.Cameras.Count,
            diagnostics.OrderBy(d => d.DistanceMeters).Take(30).ToArray(), lastWarning,
            FormattableString.Invariant($"{vehicle.X:F1}, {vehicle.Y:F1}, {vehicle.Z:F1}"), data.LatestFine, data.LatestCameraFine, data.FineHistory, data.DroppedFineCount);
    }

    public void Reset()
    {
        engine.Reset();progress.Reset();
        uncalibrated.Reset();
        planner?.ClearPlan();guidance?.Suspend("landmarks");
        previousMap = null;
        paths = null;
        lastWarning = null;
    }
}
