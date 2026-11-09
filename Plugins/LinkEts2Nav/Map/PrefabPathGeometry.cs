using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Preserves every reachable input/output lane pair and a compatible representative path.</summary>
public static class PrefabPathGeometry
{
    public static IReadOnlyList<DirectedRoad> Build(Prefab prefab,PrefabDescriptor descriptor)
    {
        int count=descriptor.Nodes.Count;
        if(count<2||prefab.Nodes.Count!=count||prefab.Origin>=count||descriptor.NavCurves.Count==0)return [];
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return [];
        var control=placement.ControlNodes;var curves=descriptor.NavCurves;
        bool Valid(int i)=>i>=0&&i<curves.Count&&Finite(curves[i].StartPosition)&&Finite(curves[i].EndPosition)
            &&float.IsFinite(curves[i].Length)&&curves[i].Length>.001f;
        var targets=control.SelectMany(n=>n.OutputLines).Where(Valid).ToHashSet();
        var searches=control.SelectMany(n=>n.InputLines).Where(Valid).Distinct()
            .ToDictionary(i=>i,i=>PrefabCurvePathfinder.FindEach(descriptor,i,targets));
        var laneConnections=PrefabLaneTopology.Build(prefab,descriptor);
        var result=new List<DirectedRoad>();
        Dictionary<int,int> Order(int node,bool input)
        {
            var lines=input?control[node].InputLines:control[node].OutputLines;
            var valid=lines.Select((line,slot)=>(line,slot)).Where(p=>Valid(p.line)).ToArray();
            if(valid.Length==0)return [];
            Vector3 Position(int line)=>placement.ToWorld(input?curves[line].StartPosition:curves[line].EndPosition);
            Vector3 heading=Vector3.Zero;
            foreach(var p in valid)
            {
                var q=input?curves[p.line].StartRotation:curves[p.line].EndRotation;
                var local=Vector3.Transform(-Vector3.UnitZ,q);
                heading+=placement.ToWorld(local)-placement.ToWorld(Vector3.Zero);
            }
            var right=new Vector3(-heading.Z,0,heading.X);
            return valid.OrderBy(p=>Vector3.Dot(Position(p.line)-prefab.Nodes[node].Position,right)).ThenBy(p=>p.slot)
                .Select((p,rank)=>(p.slot,rank)).ToDictionary(p=>p.slot,p=>p.rank);
        }
        for(int from=0;from<count;from++)
        for(int to=0;to<count;to++)
        {
            if(from==to)continue;
            var entryOrder=Order(from,true);var exitOrder=Order(to,false);
            var candidates=new List<PrefabLanePath>();
            for(int input=0;input<control[from].InputLines.Length;input++)
            for(int output=0;output<control[to].OutputLines.Length;output++)
            {
                int start=control[from].InputLines[input],end=control[to].OutputLines[output];
                if(!Valid(start)||!Valid(end)||!searches[start].TryGetValue(end,out var chain))continue;
                var points=Sample(prefab,descriptor,placement,from,to,chain);
                if(points is null)continue;
                var lanePoints=points.Skip(1).Take(points.Count-2).ToArray();
                candidates.Add(new(prefab.Uid,prefab.Nodes[from].Uid,prefab.Nodes[to].Uid,input,output,
                    entryOrder[input],exitOrder[output],entryOrder.Count,exitOrder.Count,chain,lanePoints,
                    lanePoints.Zip(lanePoints.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second))));
            }
            if(candidates.Count==0)continue;
            var representative=PrefabCurvePathfinder.Find(descriptor,control[from].InputLines,control[to].OutputLines.Where(Valid).ToHashSet());
            var referencePoints=Sample(prefab,descriptor,placement,from,to,representative);
            if(referencePoints is null)continue;
            result.Add(new($"prefab:{prefab.Uid:X16}:{from}:{to}",prefab.Nodes[from].Uid,prefab.Nodes[to].Uid,referencePoints)
                {LaneConnections=laneConnections,LanePaths=candidates.ToArray()});
        }
        return result;
    }
    private static List<Vector3>? Sample(Prefab prefab,PrefabDescriptor descriptor,PrefabPlacement placement,
        int from,int to,IReadOnlyList<int> chain)
    {
        var curves=descriptor.NavCurves;var control=placement.ControlNodes;
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
                    points.Add(placement.ToWorld(position));
                }
                if (Math.Abs(measured - curve.Length) > Math.Max(2, curve.Length * .25)) valid = false;
                if (!valid) break;
            }
            if (!valid || points.Count < 3) return null;
            // Grade the actual sampled lane chain, never an invented node-to-node edge.
            // Arc-length interpolation also handles curved/sloping native geometry.
            double total = 0;
            var along = new double[points.Count];
            for (int i = 2; i < points.Count; i++)
                along[i] = total += Vector3.Distance(points[i - 1], points[i]);
            if (!double.IsFinite(total) || total <= .001) return null;
            float startHeight = prefab.Nodes[from].Position.Y - placement.ToWorld(control[from].Position).Y;
            float endHeight = prefab.Nodes[to].Position.Y - placement.ToWorld(control[to].Position).Y;
            for (int i = 1; i < points.Count; i++)
            {
                var point = points[i];
                point.Y += startHeight + (endHeight - startHeight) * (float)(along[i] / total);
                points[i] = point;
            }
            if (Vector3.Distance(points[0], points[1]) > 20
                || Vector3.Distance(points[^1], prefab.Nodes[to].Position) > 20) return null;
            // Short lane-to-reference joins only connect the actual PPD lane endpoints
            // to the map control-node centers; the junction itself follows its curves.
            points.Add(prefab.Nodes[to].Position);
        return points;
    }

    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
