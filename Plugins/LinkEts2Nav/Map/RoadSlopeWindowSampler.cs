using System.Numerics;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record RoadSlopeWindow(double? AngleDegrees,double CoveredMeters,string? Failure,IReadOnlyList<ulong> Roads);

/// <summary>Native road geometry only: no prefab control-node to lane-endpoint connector is sampled.</summary>
public sealed class RoadSlopeWindowSampler(IReadOnlyDictionary<ulong,List<Road>> incidentRoads)
{
    private const double WindowMeters=20,SampleStepMeters=.5;
    private readonly Dictionary<(ulong Node,bool Incoming),RoadSlopeWindow> cached=[];

    public RoadSlopeWindow Sample(ulong node,bool incoming)
    {
        var key=(node,incoming);
        if(cached.TryGetValue(key,out var found))return found;
        return cached[key]=Read(node,incoming);
    }

    private RoadSlopeWindow Read(ulong node,bool incoming)
    {
        double covered=0,rise=0;var used=new HashSet<ulong>();
        RoadSlopeWindow Missing(string reason)=>new(null,covered,reason,used.ToArray());
        while(covered<WindowMeters)
        {
            var options=(incidentRoads.GetValueOrDefault(node)??[]).Where(r=>!used.Contains(r.Uid)).ToArray();
            if(options.Length!=1)return Missing(options.Length==0?"20米道路坡度窗口不完整":"20米道路坡度窗口存在多个连接");
            var road=options[0];used.Add(road.Uid);
            if(!float.IsFinite(road.Length)||road.Length<=0)return Missing("道路长度无效");
            bool forward=road.Node.Uid==node;
            int steps=Math.Clamp((int)Math.Ceiling(road.Length/SampleStepMeters),4,100_000);
            Vector3 At(int i)=>road.InterpolateCurve(forward?(float)i/steps:1-(float)i/steps).Position;
            var previous=At(0);
            for(int i=1;i<=steps&&covered<WindowMeters;i++)
            {
                var point=At(i);var delta=point-previous;
                if(!Finite(previous)||!Finite(point))return Missing("道路坡度采样包含非有限坐标");
                double horizontal=Math.Sqrt(delta.X*delta.X+delta.Z*delta.Z);
                if(horizontal<.000001){previous=point;continue;}
                double take=Math.Min(horizontal,WindowMeters-covered);
                rise+=delta.Y*take/horizontal;covered+=take;previous=point;
            }
            node=forward?road.ForwardNode.Uid:road.Node.Uid;
            // A shared node is a structural join. Never invent a connector segment
            // between two road interpolation endpoints to make the window continuous.
        }
        return new(Math.Atan2(incoming?-rise:rise,covered)*180/Math.PI,covered,null,used.ToArray());
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
