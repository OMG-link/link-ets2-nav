using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Preserves every reachable input/output lane pair and a compatible representative path.</summary>
public static class PrefabPathGeometry
{
    public static IReadOnlyList<DirectedRoad> Build(Prefab prefab,PrefabDescriptor descriptor)
        => BuildNavigation(prefab, descriptor, []).Paths;

    internal static (IReadOnlyList<DirectedRoad> Paths, NativeLaneGeometry? Geometry) BuildNavigation(
        Prefab prefab, PrefabDescriptor descriptor, IReadOnlyList<NativeContinuationOverride> continuityOverrides)
    {
        int count=descriptor.Nodes.Count;
        PrefabDescriptorInput.AssertInstance(prefab,descriptor);
        PrefabDescriptorInput.AssertCurveIndices(descriptor);
        PrefabDescriptorInput.AssertCurveGeometry(descriptor);
        PrefabDescriptorInput.AssertBlinkers(descriptor);
        if(descriptor.NavCurves.Count==0)return ([], null);
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return ([], null);
        var control=placement.ControlNodes;var curves=descriptor.NavCurves;
        var targets=control.SelectMany(n=>n.OutputLines).Where(i=>i!=-1).ToHashSet();
        var searches=control.SelectMany(n=>n.InputLines).Where(i=>i!=-1).Distinct()
            .ToDictionary(i=>i,i=>PrefabCurvePathfinder.FindEach(descriptor,i,targets,prefab.Uid));
        var laneConnections=PrefabLaneTopology.Build(prefab,descriptor);
        var junction=PrefabJunctionClassifier.Analyze(descriptor);
        var result=new List<DirectedRoad>();
        Dictionary<int,int> Order(int node,bool input)
        {
            var lines=input?control[node].InputLines:control[node].OutputLines;
            var valid=lines.Select((line,slot)=>(line,slot)).Where(p=>p.line!=-1).ToArray();
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
        var entryOrders = Enumerable.Range(0, count).Select(node => Order(node, true)).ToArray();
        var exitOrders = Enumerable.Range(0, count).Select(node => Order(node, false)).ToArray();
        NativeLanePort[] Ports(IReadOnlyList<Dictionary<int, int>> orders, bool input)
            => orders.SelectMany((slots, node) => slots.Select(slot => new NativeLanePort(
                prefab.Nodes[node].Uid, slot.Key, slot.Value,
                (input ? control[node].InputLines : control[node].OutputLines)[slot.Key]))).ToArray();
        var nativeGeometry=BuildNativeGeometry(prefab.Uid,descriptor,placement) with
        {
            ContinuationOverrides = continuityOverrides,
            Inputs = Ports(entryOrders, true),
            Outputs = Ports(exitOrders, false)
        };
        var forks=PrefabForkTopology.Build(prefab,descriptor,placement,node=>entryOrders[node]);
        var laneSignals=PrefabForkTopology.LaneSignals(prefab,descriptor,placement,node=>entryOrders[node]);
        for(int from=0;from<count;from++)
        for(int to=0;to<count;to++)
        {
            var entryOrder=entryOrders[from];var exitOrder=exitOrders[to];
            IReadOnlyList<int> Starts(IReadOnlyList<int> chain)
            {
                var indices=new List<int>();int offset=0;
                foreach(int curve in chain){indices.Add(offset);offset+=Math.Clamp((int)Math.Ceiling(curves[curve].Length/3),4,256)+1;}
                return indices.ToArray();
            }
            var candidates=new List<PrefabLanePath>();
            PrefabLanePath? representative=null;
            IReadOnlyList<Vector3>? referencePoints=null;
            float leastCost=float.PositiveInfinity;
            for(int input=0;input<control[from].InputLines.Length;input++)
            for(int output=0;output<control[to].OutputLines.Length;output++)
            {
                int start=control[from].InputLines[input],end=control[to].OutputLines[output];
                if(start==-1||end==-1||!searches[start].TryGetValue(end,out var chain))continue;
                var points=Sample(prefab,descriptor,placement,from,to,chain);
                var lanePoints=points.Skip(1).Take(points.Count-2).ToArray();
                var path=new PrefabLanePath(prefab.Uid,prefab.Nodes[from].Uid,prefab.Nodes[to].Uid,input,output,
                    entryOrder[input],exitOrder[output],entryOrder.Count,exitOrder.Count,chain,lanePoints,
                    lanePoints.Zip(lanePoints.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second)))
                    {NativeGeometry=nativeGeometry,CurveStartPointIndices=Starts(chain),CurveBlinkers=chain.Select(i=>(LaneBlinker)(int)curves[i].Blinker).ToArray(),
                        NativeBranchSignal=laneSignals.GetValueOrDefault((prefab.Nodes[from].Uid,input,prefab.Nodes[to].Uid,output))};
                candidates.Add(path);
                float cost=0;
                foreach(int index in chain)cost+=curves[index].Length;
                if(cost<leastCost)
                {
                    leastCost=cost;
                    representative=path;
                    referencePoints=points;
                }
            }
            if(representative is null)continue;
            var indices=representative.CurveIndices;
            result.Add(new($"prefab:{prefab.Uid:X16}:{from}:{to}",prefab.Nodes[from].Uid,prefab.Nodes[to].Uid,referencePoints!)
                {LaneConnections=laneConnections,LanePaths=candidates.ToArray(),Junction=junction,
                    IsMinorStraight=junction.IsMinorPassage((from+prefab.Origin)%count,(to+prefab.Origin)%count),
                    ForkInfo=forks.GetValueOrDefault(prefab.Nodes[from].Uid),
                    NativeCurveSpans=indices.Zip(representative.CurveStartPointIndices,(curve,start)=>new PrefabCurveSpan(curve,start+1)).ToArray(),
                    NativeEntryTangent=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curves[indices[0]].StartRotation)),
                    NativeExitTangent=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curves[indices[^1]].EndRotation))});
        }
        return (result, nativeGeometry);
    }
    private static NativeLaneGeometry BuildNativeGeometry(ulong prefabUid,PrefabDescriptor descriptor,PrefabPlacement placement)
    {
        // Access projection disconnects forbidden curves from all links and ports.
        // A referenced native curve may have a legitimate zero reported length.
        var curves=descriptor.NavCurves;
        var inputIndices=descriptor.Nodes.SelectMany(n=>n.InputLines).Where(i=>i!=-1).Distinct().Order().ToArray();
        var outputIndices=descriptor.Nodes.SelectMany(n=>n.OutputLines).Where(i=>i!=-1).Distinct().Order().ToArray();
        var included=inputIndices.Concat(outputIndices).ToHashSet();
        for(int index=0;index<curves.Count;index++)
        {
            foreach(int adjacent in curves[index].NextLines.Concat(curves[index].PreviousLines))
            {
                included.Add(index);
                included.Add(adjacent);
            }
        }
        var result=new List<NativeLaneCurve>();
        Vector3 Derivative(Quaternion rotation,float length)
            =>float.IsFinite(rotation.LengthSquared())&&rotation.LengthSquared()>1e-10f
                ?placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,rotation))*length:Vector3.Zero;
        foreach(int i in included.Order())
        {
            var curve=curves[i];
            result.Add(new(i,placement.ToWorld(curve.StartPosition),placement.ToWorld(curve.EndPosition),
                Derivative(curve.StartRotation,curve.Length),Derivative(curve.EndRotation,curve.Length),
                curve.NextLines.Distinct().ToArray(),curve.PreviousLines.Distinct().ToArray())
                {Blinker=(LaneBlinker)(int)curve.Blinker});
        }
        return new(prefabUid,result.ToArray())
        {
            InputCurveIndices=inputIndices,
            OutputCurveIndices=outputIndices
        };
    }

    private static List<Vector3> Sample(Prefab prefab,PrefabDescriptor descriptor,PrefabPlacement placement,
        int from,int to,IReadOnlyList<int> chain)
    {
        var curves=descriptor.NavCurves;var control=placement.ControlNodes;
            var points = new List<Vector3> { prefab.Nodes[from].Position };
            foreach (int index in chain)
            {
                var curve = curves[index];
                var startDirection = Vector3.Transform(-Vector3.UnitZ, curve.StartRotation);
                var endDirection = Vector3.Transform(-Vector3.UnitZ, curve.EndRotation);
                if (!Finite(startDirection) || !Finite(endDirection)
                    || !Finite(placement.ToWorldDirection(startDirection)) || !Finite(placement.ToWorldDirection(endDirection)))
                    throw Unsupported($"curve {index} has a nonfinite endpoint tangent");
                int samples = Math.Clamp((int)Math.Ceiling(curve.Length / 3), 4, 256);
                for (int i = 0; i <= samples; i++)
                {
                    float t = (float)i / samples, t2 = t * t, t3 = t2 * t;
                    var position = (2*t3 - 3*t2 + 1)*curve.StartPosition
                        + (t3 - 2*t2 + t)*startDirection*curve.Length
                        + (-2*t3 + 3*t2)*curve.EndPosition
                        + (t3 - t2)*endDirection*curve.Length;
                    var worldPosition=placement.ToWorld(position);
                    if (!Finite(worldPosition))
                        throw Unsupported($"curve {index} at t={t} has nonfinite sampled coordinates");
                    points.Add(worldPosition);
                }
            }
            if (points.Count < 3) throw Unsupported("passage geometry has fewer than three points");
            // Grade the actual sampled lane chain, never an invented node-to-node edge.
            // Arc-length interpolation also handles curved/sloping native geometry.
            double total = 0;
            var along = new double[points.Count];
            for (int i = 2; i < points.Count; i++)
                along[i] = total += Vector3.Distance(points[i - 1], points[i]);
            if (!double.IsFinite(total) || total <= .001)
                throw Unsupported($"passage geometry has unusable sampled length {total} m");
            float startHeight = prefab.Nodes[from].Position.Y - placement.ToWorld(control[from].Position).Y;
            float endHeight = prefab.Nodes[to].Position.Y - placement.ToWorld(control[to].Position).Y;
            for (int i = 1; i < points.Count; i++)
            {
                var point = points[i];
                point.Y += startHeight + (endHeight - startHeight) * (float)(along[i] / total);
                if (!Finite(point)) throw Unsupported($"graded sample {i} has nonfinite coordinates");
                points[i] = point;
            }
            // Short lane-to-reference joins only connect the actual PPD lane endpoints
            // to the map control-node centers; the junction itself follows its curves.
            points.Add(prefab.Nodes[to].Position);
        return points;

        NotSupportedException Unsupported(string reason)
            =>new($"Prefab {prefab.Uid:X16}, interfaces {prefab.Nodes[from].Uid:X16} → {prefab.Nodes[to].Uid:X16}, curves [{string.Join(',',chain)}]: {reason}.");
    }

    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
