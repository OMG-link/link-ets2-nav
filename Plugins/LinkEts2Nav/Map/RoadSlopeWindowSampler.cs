using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record RoadSlopeWindow(double? AngleDegrees,double CoveredMeters,string? Failure,IReadOnlyList<ulong> Roads)
{
    public IReadOnlyList<ulong> Prefabs { get; init; }=[];
}

/// <summary>Measures only native road and PPD geometry. Shared interface UIDs add no connector mileage.</summary>
public sealed class RoadSlopeWindowSampler(IReadOnlyDictionary<ulong,List<Road>> incidentRoads,
    IReadOnlyList<DirectedRoad>? prefabPaths=null)
{
    private const double WindowMeters=20,SampleStepMeters=.5,ParallelSlopeToleranceDegrees=.1;
    private readonly Dictionary<(ulong Node,bool Incoming,string? Exclude),RoadSlopeWindow> cached=[];
    private readonly ILookup<ulong,DirectedRoad> incomingPrefabs=(prefabPaths??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null).ToLookup(e=>e.EndNodeUid);
    private readonly ILookup<ulong,DirectedRoad> outgoingPrefabs=(prefabPaths??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null).ToLookup(e=>e.StartNodeUid);

    public RoadSlopeWindow Sample(ulong node,bool incoming,string? excludePrefab=null)
    {
        var key=(node,incoming,excludePrefab);
        if(cached.TryGetValue(key,out var found))return found;
        return cached[key]=Read(node,incoming,excludePrefab,null,0,0,[],[]);
    }

    private RoadSlopeWindow Read(ulong node,bool incoming,string? excludePrefab,int? lane,
        double covered,double rise,HashSet<ulong> roads,HashSet<ulong> usedPrefabs)
    {
        RoadSlopeWindow Result(double? angle,string? failure)=>new(angle,covered,failure,roads.ToArray())
            {Prefabs=usedPrefabs.ToArray()};
        if(covered>=WindowMeters)return Result(Math.Atan2(rise,covered)*180/Math.PI,null);
        var options=(incidentRoads.GetValueOrDefault(node)??[]).Where(r=>!roads.Contains(r.Uid)).ToArray();
        if(options.Length>1)return Result(null,"20米道路坡度窗口存在多个连接");
        if(options.Length==1)
        {
            var road=options[0];roads.Add(road.Uid);
            if(!float.IsFinite(road.Length)||road.Length<=0)return Result(null,"道路长度无效");
            bool forward=road.Node.Uid==node;
            int steps=Math.Clamp((int)Math.Ceiling(road.Length/SampleStepMeters),4,100_000);
            Vector3 At(int i)=>road.InterpolateCurve(forward?(float)i/steps:1-(float)i/steps).Position;
            var previous=At(0);
            for(int i=1;i<=steps&&covered<WindowMeters;i++)
            {
                var point=At(i);
                if(!Add(previous,point,incoming,ref covered,ref rise))return Result(null,"道路坡度采样包含非有限坐标");
                previous=point;
            }
            return Read(forward?road.ForwardNode.Uid:road.Node.Uid,incoming,excludePrefab,lane,covered,rise,roads,usedPrefabs);
        }

        var edges=(incoming?incomingPrefabs:outgoingPrefabs)[node].Where(e=>NativeLaneGraph.Prefab(e)!=excludePrefab&&!usedPrefabs.Contains(e.LanePaths.FirstOrDefault()?.PrefabUid??0))
            .Where(e=>e.LanePaths.Any(p=>lane is null||(incoming?p.ExitOrder:p.EntryOrder)==lane)).ToArray();
        if(edges.Length==0)return Result(null,"20米道路坡度窗口不完整（缺少相接道路或原生预制件车道）");
        // Distinct upstream/downstream ports are real route alternatives, not interchangeable lanes.
        if(edges.Select(e=>incoming?e.StartNodeUid:e.EndNodeUid).Distinct().Count()!=1)
            return Result(null,"20米预制件坡度窗口存在多个支路接口");
        var paths=edges.SelectMany(e=>e.LanePaths).Where(p=>lane is null||(incoming?p.ExitOrder:p.EntryOrder)==lane)
            .DistinctBy(p=>(p.PrefabUid,p.EntryLane,p.ExitLane,string.Join(',',p.CurveIndices))).ToArray();
        var windows=new List<RoadSlopeWindow>();
        foreach(var path in paths)
        {
            double distance=covered,height=rise;
            var visited=new HashSet<ulong>(usedPrefabs){path.PrefabUid};
            var points=path.Points;
            for(int step=1;step<points.Count&&distance<WindowMeters;step++)
            {
                int i=incoming?points.Count-1-step:step;
                int previous=incoming?i+1:i-1;
                // A curve-start gap is not part of either native curve.
                if(path.CurveStartPointIndices.Contains(Math.Max(i,previous)))continue;
                if(!Add(points[previous],points[i],incoming,ref distance,ref height))
                    return Result(null,"预制件坡度采样包含非有限坐标");
            }
            windows.Add(Read(incoming?path.EntryNodeUid:path.ExitNodeUid,incoming,excludePrefab,
                incoming?path.EntryOrder:path.ExitOrder,distance,height,new(roads),visited));
        }
        var failed=windows.FirstOrDefault(w=>w.Failure is not null);
        if(failed is not null)return failed;
        if(windows.Max(w=>w.AngleDegrees!.Value)-windows.Min(w=>w.AngleDegrees!.Value)>ParallelSlopeToleranceDegrees)
            return Result(null,"20米预制件坡度窗口存在不同坡度的车道候选");
        // Parallel lane centers measure the same interface; average their independent native windows.
        return new(windows.Average(w=>w.AngleDegrees!.Value),WindowMeters,null,
            windows.SelectMany(w=>w.Roads).Distinct().ToArray())
            {Prefabs=windows.SelectMany(w=>w.Prefabs).Distinct().ToArray()};
    }

    private static bool Add(Vector3 from,Vector3 to,bool incoming,ref double covered,ref double rise)
    {
        if(!Finite(from)||!Finite(to))return false;
        var delta=to-from;
        double horizontal=Math.Sqrt((double)delta.X*delta.X+(double)delta.Z*delta.Z);
        if(horizontal<.000001)return true;
        double take=Math.Min(horizontal,WindowMeters-covered);
        rise+=(incoming?-delta.Y:delta.Y)*take/horizontal;covered+=take;
        return true;
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
