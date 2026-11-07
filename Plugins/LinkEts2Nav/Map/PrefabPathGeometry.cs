using System.Numerics;
using TruckLib;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Map;

/// <summary>Directed junction connections sampled from actual PPD navigation curves.</summary>
public static class PrefabPathGeometry
{
    public static IReadOnlyList<DirectedRoad> Build(Prefab prefab, PrefabDescriptor descriptor)
    {
        int count = descriptor.Nodes.Count;
        if (count < 2 || prefab.Nodes.Count != count || prefab.Origin >= count || descriptor.NavCurves.Count == 0) return [];
        var control = Enumerable.Range(0, count).Select(i => descriptor.Nodes[(i + prefab.Origin) % count]).ToArray();
        var localOrigin = control[0].Position;
        var worldOrigin = prefab.Nodes[0].Position;
        int other = Enumerable.Range(1, count - 1).MaxBy(i => Horizontal(control[i].Position - localOrigin));
        var localDelta = control[other].Position - localOrigin;
        var worldDelta = prefab.Nodes[other].Position - worldOrigin;
        float localLength = Horizontal(localDelta), worldLength = Horizontal(worldDelta);
        if (localLength < 1 || Math.Abs(worldLength - localLength) > .5f) return [];
        float yaw = MathF.Atan2(worldDelta.Z, worldDelta.X) - MathF.Atan2(localDelta.Z, localDelta.X);
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        Vector3 World(Vector3 point)
        {
            var p = point - localOrigin;
            return worldOrigin + new Vector3(p.X * cos - p.Z * sin, p.Y, p.X * sin + p.Z * cos);
        }
        // Reject deformed/unsupported placements rather than silently using a wrong transform.
        for (int i = 0; i < count; i++)
            if (!Finite(prefab.Nodes[i].Position) || Vector3.Distance(World(control[i].Position), prefab.Nodes[i].Position) > .5f) return [];

        var curves = descriptor.NavCurves;
        bool Valid(int i) => i >= 0 && i < curves.Count && Finite(curves[i].StartPosition)
            && Finite(curves[i].EndPosition) && float.IsFinite(curves[i].Length) && curves[i].Length > .001f;
        var result = new List<DirectedRoad>();
        for (int from = 0; from < count; from++)
        for (int to = 0; to < count; to++)
        {
            if (from == to) continue;
            var targets = control[to].OutputLines.Where(Valid).ToHashSet();
            if (targets.Count == 0) continue;
            var queue = new PriorityQueue<int, float>();
            var costs = new Dictionary<int, float>();
            var previous = new Dictionary<int, int>();
            foreach (int line in control[from].InputLines.Where(Valid).Distinct())
            { costs[line] = curves[line].Length; previous[line] = -1; queue.Enqueue(line, costs[line]); }
            int found = -1, visited = 0;
            while (queue.TryDequeue(out int line, out float cost) && visited++ < 8192)
            {
                if (cost > costs[line] || cost > 2500) continue;
                if (targets.Contains(line)) { found = line; break; }
                foreach (int next in curves[line].NextLines.Where(Valid))
                {
                    if (Vector3.Distance(curves[line].EndPosition, curves[next].StartPosition) > .5f) continue;
                    float nextCost = cost + curves[next].Length;
                    if (costs.TryGetValue(next, out float known) && known <= nextCost) continue;
                    costs[next] = nextCost; previous[next] = line; queue.Enqueue(next, nextCost);
                }
            }
            if (found < 0) continue;
            var chain = new List<int>();
            for (int current = found; current >= 0; current = previous[current])
            { chain.Add(current); if (chain.Count > curves.Count) break; }
            if (chain.Count > curves.Count) continue;
            chain.Reverse();
            var points = new List<Vector3> { prefab.Nodes[from].Position };
            bool valid = true;
            foreach (int index in chain)
            {
                var curve = curves[index];
                var startDirection = Vector3.Transform(-Vector3.UnitZ, curve.StartRotation);
                var endDirection = Vector3.Transform(-Vector3.UnitZ, curve.EndRotation);
                if (!Finite(startDirection) || !Finite(endDirection)) { valid = false; break; }
                int samples = Math.Clamp((int)Math.Ceiling(curve.Length / 3), 4, 256);
                double measured = 0;
                Vector3 last = default;
                for (int i = 0; i <= samples; i++)
                {
                    float t = (float)i / samples, t2 = t * t, t3 = t2 * t;
                    var position = (2*t3 - 3*t2 + 1)*curve.StartPosition
                        + (t3 - 2*t2 + t)*startDirection*curve.Length
                        + (-2*t3 + 3*t2)*curve.EndPosition
                        + (t3 - t2)*endDirection*curve.Length;
                    if (!Finite(position)) { valid = false; break; }
                    if (i > 0) measured += Vector3.Distance(last, position);
                    last = position;
                    points.Add(World(position));
                }
                if (Math.Abs(measured - curve.Length) > Math.Max(2, curve.Length * .25)) valid = false;
                if (!valid) break;
            }
            if (!valid || points.Count < 3 || Vector3.Distance(points[0], points[1]) > 20
                || Vector3.Distance(points[^1], prefab.Nodes[to].Position) > 20) continue;
            // Short lane-to-reference joins only connect the actual PPD lane endpoints
            // to the map control-node centers; the junction itself follows its curves.
            points.Add(prefab.Nodes[to].Position);
            result.Add(new($"prefab:{prefab.Uid:X16}:{from}:{to}",
                prefab.Nodes[from].Uid, prefab.Nodes[to].Uid, points));
        }
        return result;
    }

    private static float Horizontal(Vector3 p) => MathF.Sqrt(p.X*p.X+p.Z*p.Z);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
