using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Reconstructs a declared single travel lane only when both ends have matching native PPD anchors.</summary>
internal sealed class AnchoredRoadLanes
{
    internal sealed record Lane(IReadOnlyList<Vector3> Midline, float OffsetUncertainty, bool StableCenter);
    private readonly record struct PortOffset(float Lateral, float Residual);
    private readonly IReadOnlyDictionary<ulong, List<Road>> incident;
    private readonly Func<Road, bool, IReadOnlyList<bool?>?> access;
    private readonly ILookup<ulong, PrefabLanePath> entrances, exits;
    private readonly Dictionary<(ulong Road, bool Forward), Lane?> cache = [];

    public AnchoredRoadLanes(IReadOnlyDictionary<ulong, List<Road>> incident, PrefabLanePositionIndex prefabs,
        Func<Road, bool, IReadOnlyList<bool?>?> access)
    {
        this.incident = incident;
        this.access = access;
        entrances = prefabs.Ports(inputs: true);
        exits = prefabs.Ports(inputs: false);
    }

    public Lane? Locate(Road road, bool forward, int intervals)
    {
        if (cache.TryGetValue((road.Uid, forward), out var cached)) return cached;
        return cache[(road.Uid, forward)] = Build();

        Lane? Build()
        {
            if (access(road, forward) is not [not false] || intervals < 1) return null;
            var start = Offsets(road, forward, atEntry: true);
            var end = Offsets(road, forward, atEntry: false);
            var common = (from a in start from b in end where Math.Abs(a.Lateral - b.Lateral) <= .25f select (a,b)).ToArray();
            float offset, uncertainty;
            bool stable = common.Length > 0 && common.Max(p => Math.Max(p.a.Lateral,p.b.Lateral))
                - common.Min(p => Math.Min(p.a.Lateral,p.b.Lateral)) <= .25f;
            if (stable)
            {
                float min = common.Min(p => Math.Min(p.a.Lateral,p.b.Lateral));
                float max = common.Max(p => Math.Max(p.a.Lateral,p.b.Lateral));
                offset = (min + max) / 2;
                uncertainty = (max - min) / 2 + common.Max(p => Math.Max(p.a.Residual,p.b.Residual));
            }
            else
            {
                // The same one-lane layout may meet different offsets at its two ends.
                // Retain their full span only when each end independently identifies one side.
                if (start.Count == 0 || end.Count == 0 || start.Max(p => p.Lateral) - start.Min(p => p.Lateral) > .25f
                    || end.Max(p => p.Lateral) - end.Min(p => p.Lateral) > .25f) return null;
                float min = Math.Min(start.Min(p => p.Lateral), end.Min(p => p.Lateral));
                float max = Math.Max(start.Max(p => p.Lateral), end.Max(p => p.Lateral));
                if (min * max <= 0) return null;
                offset = (min + max) / 2;
                uncertainty = (max - min) / 2 + Math.Max(start.Max(p => p.Residual), end.Max(p => p.Residual));
            }
            Vector3 Point(float t)
            {
                var pose = road.InterpolateCurve(t);
                var normal = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, pose.Rotation));
                return pose.Position + normal * (forward ? offset : -offset);
            }
            var midline = Enumerable.Range(0, intervals + 1).Select(i => Point((float)i / intervals)).ToArray();
            float chordError = 0;
            for (int i = 0; i < intervals; i++)
            for (int j = 1; j < 4; j++)
            {
                float fraction = j / 4f;
                var chord = Vector3.Lerp(midline[i],midline[i + 1],fraction);
                chordError = Math.Max(chordError,Vector3.Distance(chord,Point((i + fraction) / intervals)));
            }
            return new(midline, uncertainty + chordError, stable);
        }
    }

    private List<PortOffset> Offsets(Road target, bool forward, bool atEntry)
    {
        var current = target;
        bool throughForward = forward;
        var seen = new HashSet<ulong>();
        while (seen.Add(current.Uid))
        {
            bool atNode = atEntry == throughForward;
            var node = atNode ? current.Node : current.ForwardNode;
            if (!incident.TryGetValue(node.Uid, out var roads)) break;
            if (roads.Count == 1)
            {
                var pose = current.InterpolateCurve(atNode ? 0 : 1);
                var normal = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, pose.Rotation)) * (throughForward ? 1 : -1);
                var middle = current.InterpolateCurve(atNode ? .001f : .999f).Position;
                var travel = Vector3.Normalize(middle - pose.Position) * (atEntry ? 1 : -1);
                var offsets = new List<PortOffset>();
                foreach (var lane in atEntry ? exits[node.Uid] : entrances[node.Uid])
                {
                    if (lane.Points.Count < 2) continue;
                    var endpoint = atEntry ? lane.Points[^1] : lane.Points[0];
                    var tangent = atEntry ? endpoint - lane.Points[^2] : lane.Points[1] - endpoint;
                    var delta = endpoint - pose.Position;
                    float offset = Vector3.Dot(delta, normal);
                    // The anchor must lie on this cross-section and face along the road.
                    float residual = (delta - normal * offset).Length();
                    if (Vector3.Dot(Vector3.Normalize(tangent), travel) >= .5f && residual <= .5f)
                        offsets.Add(new(offset,residual));
                }
                return offsets;
            }
            if (roads.Count != 2 || entrances.Contains(node.Uid) || exits.Contains(node.Uid)) break;
            var next = roads[0].Uid == current.Uid ? roads[1] : roads[0];
            if (next.RoadType != current.RoadType || next.LeftHandTraffic != current.LeftHandTraffic
                || !SameSide(next.Left, current.Left) || !SameSide(next.Right, current.Right)) break;
            throughForward = atEntry ? next.ForwardNode.Uid == node.Uid : next.Node.Uid == node.Uid;
            if (access(next, throughForward) is not [not false]) break;
            current = next;
        }
        return [];
    }

    private static bool SameSide(RoadSide a, RoadSide b)
        => a.TrafficRule == b.TrafficRule && a.Variant == b.Variant
            && a.VariantOverrides.Count == 0 && b.VariantOverrides.Count == 0;
}
