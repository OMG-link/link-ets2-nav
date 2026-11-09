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
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return [];
        var control=placement.ControlNodes;
        Vector3 World(Vector3 point)=>placement.ToWorld(point);

        var laneConnections = PrefabLaneTopology.Build(prefab, descriptor);
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
            var chain = PrefabCurvePathfinder.Find(descriptor,control[from].InputLines,targets);
            if(chain.Count==0)continue;
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
            if (!valid || points.Count < 3) continue;
            // Grade the actual sampled lane chain, never an invented node-to-node edge.
            // Arc-length interpolation also handles curved/sloping native geometry.
            double total = 0;
            var along = new double[points.Count];
            for (int i = 2; i < points.Count; i++)
                along[i] = total += Vector3.Distance(points[i - 1], points[i]);
            if (!double.IsFinite(total) || total <= .001) continue;
            float startHeight = prefab.Nodes[from].Position.Y - World(control[from].Position).Y;
            float endHeight = prefab.Nodes[to].Position.Y - World(control[to].Position).Y;
            for (int i = 1; i < points.Count; i++)
            {
                var point = points[i];
                point.Y += startHeight + (endHeight - startHeight) * (float)(along[i] / total);
                points[i] = point;
            }
            if (Vector3.Distance(points[0], points[1]) > 20
                || Vector3.Distance(points[^1], prefab.Nodes[to].Position) > 20) continue;
            // Short lane-to-reference joins only connect the actual PPD lane endpoints
            // to the map control-node centers; the junction itself follows its curves.
            points.Add(prefab.Nodes[to].Position);
            result.Add(new($"prefab:{prefab.Uid:X16}:{from}:{to}",
                prefab.Nodes[from].Uid, prefab.Nodes[to].Uid, points) { LaneConnections = laneConnections });
        }
        return result;
    }

    private static float Horizontal(Vector3 p) => MathF.Sqrt(p.X*p.X+p.Z*p.Z);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
