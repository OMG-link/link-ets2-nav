using System.Numerics;
using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Probe;

internal static class RandomRouteAudit
{
    private const double SpeedMetersPerSecond = 12;
    // Measured 3D tolerance of matching PPD endpoints. Keep direct prefab joins conservative.
    private const double PrefabSeamMeters = .55;
    private static readonly double[] VisitFractions = [.2, .5, .8];
    private sealed record Step(ulong Owner, ulong From, ulong To, int Entry, int Exit, int Inputs, int Outputs,
        double Meters, Road? Road, PrefabLanePath? Lane);

    public static int Run(NearbyCameraScanner scanner, Vector3 center, float radius, int count, int seed, string output)
    {
        var random = new Random(seed);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<object>();
        var unmatched = new List<object>();
        var unmatchedSamples = new List<object>();
        var unplanned = new List<object>();
        long frames = 0, matchedFrames = 0, completeLaneFrames = 0;
        int attempts = 0, finished = 0, matchedRoutes = 0, routesWithPrefabs = 0, prefabStarts = 0;
        string stage = "map scan";
        try
        {
            var map = scanner.ReadNearby(center, radius);
            stage = "route generation";
            var data = scanner.Data;
            double sdkScale = map.Map.NormalScale;
            if (!double.IsFinite(sdkScale) || sdkScale <= 0) throw new InvalidDataException("Invalid world-to-SDK distance scale.");
            var estimator = scanner.JourneyEstimator ?? new RouteJourneyEstimator(data.Map, data.SpeedLimits.RoadCategoryLimit,
                JourneyEstimationCalibration.PrefabDistanceFactor, scanner.Ferries, (p,a,b) => data.GetPrefabPassageLengths(p.Uid,a,b));
            var steps = Traversals(map, data).OrderBy(s => s.Owner).ThenBy(s => s.From).ThenBy(s => s.To)
                .ThenBy(s => s.Entry).ThenBy(s => s.Exit).ToArray();
            var successors = steps.ToLookup(step => step.From);
            var starts = steps.Where(s => s.Lane is not null || s.Entry == s.Exit).ToArray();
            while (starts.Length > 0 && seen.Count < count && attempts < (long)count * 100)
            {
                attempts++;
                var walk = new List<Step> { starts[random.Next(starts.Length)] };
                int limit = random.Next(1, 49);
                while (walk.Count < limit)
                {
                    var last = walk[^1];
                    var next = successors[last.To].Where(s => Connects(last, s)).ToArray();
                    if (next.Length == 0) break;
                    walk.Add(next[random.Next(next.Length)]);
                }
                var nodes = Nodes(walk, sdkScale);
                string key = string.Join('-', nodes.Select(n => n.Uid.ToString("X16")));
                if (!seen.Add(key)) continue;
                if (walk.Any(s => s.Lane is not null)) routesWithPrefabs++;
                if (walk[0].Lane is not null) prefabStarts++;
                int samples = 0;
                var missingFractions = new Dictionary<int, List<double>>();
                bool matched = false;
                bool planned = false;
                string? lastState = null;
                try
                {
                    foreach (var (nav, hasStructure) in Replay(walk, nodes, map, estimator, sdkScale))
                    {
                        int frame = samples++;
                        frames++;
                        planned |= hasStructure;
                        lastState = nav.State;
                        if (nav.ConfirmedAheadMeters.HasValue) { matchedFrames++; matched = true; }
                        else
                        {
                            int visit = frame / VisitFractions.Length;
                            if (!missingFractions.TryGetValue(visit, out var fractions))
                            {
                                fractions = [];
                                missingFractions.Add(visit, fractions);
                            }
                            fractions.Add(VisitFractions[frame % VisitFractions.Length]);
                        }
                        if (nav.LaneRoute?.Complete == true) completeLaneFrames++;
                    }
                    if (matched) matchedRoutes++;
                    else unmatched.Add(new { attempt = attempts, nodes = key, state = lastState, steps = walk.Select(Witness) });
                    if (!planned) unplanned.Add(new { attempt = attempts, nodes = key, steps = walk.Select(Witness) });
                    finished++;
                }
                catch (Exception error)
                {
                    int visit = samples / VisitFractions.Length;
                    failures.Add(new { stage = "navigation", attempt = attempts, visit, sample = samples % VisitFractions.Length,
                        nodes = key, step = visit < walk.Count ? Witness(walk[visit]) : null, error = error.ToString() });
                }
                if (missingFractions.Count > 0)
                    unmatchedSamples.Add(new { attempt = attempts, nodes = key, visits = missingFractions.Select(p => new {
                        visit = p.Key, from = walk[p.Key].From.ToString("X16"), to = walk[p.Key].To.ToString("X16"),
                        fractions = p.Value, step = Witness(walk[p.Key])
                    }).ToArray() });
            }
        }
        catch (Exception error)
        {
            failures.Add(new { stage, error = error.ToString() });
        }

        var report = new { x = center.X, z = center.Z, radius, seed, requested = count, attempts,
            generated = seen.Count, finished, plannedRoutes = finished - unplanned.Count, matchedRoutes, routesWithPrefabs,
            prefabStarts, frames, matchedFrames, completeLaneFrames, failures, unplanned, unmatched, unmatchedSamples };
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Seed {seed}: generated {seen.Count}/{count}, finished {finished}, unplanned {unplanned.Count}, matched {matchedRoutes}, failures {failures.Count}. {path}");
        if (finished != count || failures.Count != 0 || unplanned.Count != 0) return 1;
        return unmatched.Count == 0 ? 0 : 2;
    }

    private static IEnumerable<(NavigationDiagnostic Navigation, bool HasStructure)> Replay(IReadOnlyList<Step> walk, RouteNode[] nodes,
        NearbyMapSnapshot map, RouteJourneyEstimator estimator, double sdkScale)
    {
        var state = NavigationRouteState.Empty;
        var guidance = new GuidanceCoordinator { JourneyEstimateProvider = data => estimator.Estimate(data.Route) };
        var runtime = new NavigationRuntime(guidance: guidance);
        var warnings = new WarningPipeline(guidance: guidance);
        var signals = new TrafficLightReminderRuntime(guidance, new());
        try
        {
            for (int visit = 0; visit < walk.Count; visit++)
            {
                foreach (double fraction in VisitFractions)
                {
                    double remaining = nodes[visit].DistanceToEndMeters - walk[visit].Meters * fraction * sdkScale;
                    double seconds = (nodes[0].DistanceToEndMeters - remaining) / (sdkScale * SpeedMetersPerSecond);
                    var now = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
                    var route = RouteSnapshot.Parse(nodes.Skip(visit), now);
                    state = state.Accept(route);
                    var vehicle = Pose(walk[visit], fraction, remaining, now);
                    var data = new GameDataSnapshot(vehicle, route, route.Availability, "route-audit", true) { NavigationRoute = state };
                    guidance.Observe(data, now);
                    var nav = runtime.Process(data, map, now);
                    warnings.Process(data, map, now);
                    signals.Process(data, map, nav, now);
                    guidance.Tick(data, now);
                    yield return (nav, runtime.StructureBuildCount > 0);
                }
            }
        }
        finally
        {
            runtime.Reset();
            warnings.Reset();
        }
    }

    private static object Witness(Step step) => new
    {
        owner = step.Owner.ToString("X16"), type = step.Road is null ? "prefab" : "road",
        step.Entry, step.Exit, step.Meters, curves = step.Lane?.CurveIndices,
        roadStart = step.Road?.Node.GetType().Name, roadEnd = step.Road?.ForwardNode.GetType().Name
    };

    private static IEnumerable<Step> Traversals(NearbyMapSnapshot map, GameDataManager data)
    {
        foreach (var road in map.Map.MapItems.Values.OfType<Road>().Where(r => r.ShowInUiMap && !r.GpsAvoid))
        {
            if (road.Node is not Node || road.ForwardNode is not Node || !float.IsFinite(road.Length) || road.Length <= 0) continue;
            var traffic = data.SpeedLimits.RoadTrafficDirections(road);
            foreach (bool forward in new[] { true, false })
            {
                if ((forward ? traffic.Forward : traffic.Reverse) != true ||
                    data.SpeedLimits.RoadLanePlayerAccess(road, forward) is not { Count: > 0 } lanes) continue;
                var from = forward ? road.Node : road.ForwardNode;
                var to = forward ? road.ForwardNode : road.Node;
                for (int entry = 0; entry < lanes.Count; entry++)
                    for (int exit = 0; exit < lanes.Count; exit++)
                        if (Enumerable.Range(Math.Min(entry, exit), Math.Abs(entry - exit) + 1).All(i => lanes[i] != false))
                            yield return new(road.Uid, from.Uid, to.Uid, entry, exit, lanes.Count, lanes.Count, road.Length, road, null);
            }
        }
        foreach (var group in map.PrefabPaths.SelectMany(e => e.LanePaths).GroupBy(p => p.PrefabUid))
        {
            var prefab = data.GetPrefab(group.Key)!;
            if (!prefab.Instance.ShowInUiMap) continue;
            // Access filtering can renumber a surviving lane. Match its original PPD slot before comparing road lane order.
            var physical = PrefabPathGeometry.Build(prefab.Instance, prefab.Description!).SelectMany(e => e.LanePaths)
                .ToDictionary(p => (p.EntryNodeUid, p.EntryLane, p.ExitNodeUid, p.ExitLane));
            foreach (var lane in group)
            {
                if (data.Map.GetNode(lane.EntryNodeUid) is not Node || data.Map.GetNode(lane.ExitNodeUid) is not Node ||
                    !double.IsFinite(lane.LengthMeters) || lane.LengthMeters <= 0) continue;
                var port = physical[(lane.EntryNodeUid, lane.EntryLane, lane.ExitNodeUid, lane.ExitLane)];
                yield return new(group.Key, lane.EntryNodeUid, lane.ExitNodeUid, port.EntryOrder, port.ExitOrder,
                    port.EntryLaneCount, port.ExitLaneCount, lane.LengthMeters, null, lane);
            }
        }
    }

    private static bool Connects(Step before, Step after)
    {
        if (before.Owner == after.Owner) return false;
        if (before.Lane is {} left && after.Lane is {} right)
            return left.ExitLane == right.EntryLane && Vector3.Distance(left.Points[^1], right.Points[0]) <= PrefabSeamMeters;
        return before.Outputs == after.Inputs && before.Exit == after.Entry;
    }

    private static RouteNode[] Nodes(List<Step> walk, double sdkScale)
    {
        var nodes = new RouteNode[walk.Count + 1];
        nodes[^1] = new(walk[^1].To, 0, 0);
        double remaining = 0;
        for (int i = walk.Count - 1; i >= 0; i--)
        {
            remaining += walk[i].Meters * sdkScale;
            nodes[i] = new(walk[i].From, (float)remaining, (float)(remaining / SpeedMetersPerSecond));
        }
        return nodes;
    }

    private static VehicleSnapshot Pose(Step step, double fraction, double remaining, DateTimeOffset now)
    {
        Vector3 position, direction;
        if (step.Road is {} road)
        {
            bool forward = step.From == road.Node.Uid;
            float t = (float)(forward ? fraction : 1 - fraction);
            position = road.InterpolateCurve(t).Position;
            direction = road.InterpolateCurve(t + .001f).Position - road.InterpolateCurve(t - .001f).Position;
            if (!forward) direction = -direction;
        }
        else
        {
            var points = step.Lane!.Points;
            double ahead = step.Meters * fraction;
            int i = 1;
            for (; i < points.Count; i++)
            {
                double length = Vector3.Distance(points[i-1], points[i]);
                if (length > 0 && ahead <= length) break;
                ahead -= length;
            }
            if (i == points.Count) throw new InvalidDataException($"Lane {step.Owner:X16} has no segment at fraction {fraction}.");
            direction = points[i] - points[i - 1];
            position = Vector3.Lerp(points[i - 1], points[i], (float)(ahead / direction.Length()));
        }
        return new(position.X, position.Y, position.Z, Math.Atan2(-direction.X, -direction.Z) / Math.Tau, SpeedMetersPerSecond,
            remaining, true, false, (ulong)(now - DateTimeOffset.UnixEpoch).Ticks / 10, now);
    }
}
