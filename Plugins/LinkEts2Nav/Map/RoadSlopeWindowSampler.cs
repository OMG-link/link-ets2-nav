using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record RoadSlopeWindow(double? AngleDegrees,double CoveredMeters,string? Failure,IReadOnlyList<ulong> Roads)
{
    public IReadOnlyList<ulong> Prefabs { get; init; }=[];
    public MissingMapObjects? MapIssue {get;init;}
    public bool InconsistentLaneSlopes { get; init; }
}

/// <summary>Measures only native road and PPD geometry. Shared interface UIDs add no connector mileage.</summary>
public sealed class RoadSlopeWindowSampler(IReadOnlyDictionary<ulong,List<Road>> incidentRoads,
    IReadOnlyList<DirectedRoad>? prefabPaths=null,Func<ulong,MapQueryIssue>? missingConnection=null)
{
    internal const double WindowMeters=20;
    private const double SampleStepMeters=.5,ParallelSlopeToleranceDegrees=.1,NearZeroHorizontalMeters=.000001;
    private readonly Dictionary<(ulong Node,bool Incoming,string? Exclude,bool Common),RoadSlopeWindow> cached=[];
    private readonly ILookup<ulong,DirectedRoad> incomingPrefabs=(prefabPaths??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null).ToLookup(e=>e.EndNodeUid);
    private readonly ILookup<ulong,DirectedRoad> outgoingPrefabs=(prefabPaths??[]).Where(e=>NativeLaneGraph.Prefab(e) is not null).ToLookup(e=>e.StartNodeUid);
    private readonly HashSet<(string Prefab,ulong Entry,ulong Exit)> rampPassages=RampPassages(prefabPaths??[]);

    public RoadSlopeWindow Sample(ulong node,bool incoming,string? excludePrefab=null,bool common=false)
    {
        var key=(node,incoming,excludePrefab,common);
        if(cached.TryGetValue(key,out var found))return found;
        var sample=Read(node,incoming,excludePrefab,common,null,0,0,[],[]);
        var road=(incidentRoads.GetValueOrDefault(node)??[]).FirstOrDefault();
        Vector3? anchor=road is not null?(road.Node.Uid==node?road.Node.Position:road.ForwardNode.Position)
            :(prefabPaths??[]).Where(e=>e.Points.Count>0&&(e.StartNodeUid==node||e.EndNodeUid==node))
                .Select(e=>(Vector3?)(e.StartNodeUid==node?e.Points[0]:e.Points[^1])).FirstOrDefault();
        if(sample.MapIssue is {} issue)sample=sample with{MapIssue=issue with{QueryAnchor=anchor??issue.QueryAnchor}};
        return cached[key]=sample;
    }

    private RoadSlopeWindow Read(ulong node,bool incoming,string? excludePrefab,bool common,int? lane,
        double covered,double rise,HashSet<ulong> roads,HashSet<ulong> usedPrefabs)
    {
        RoadSlopeWindow Result(double? angle,MissingMapObjects? dependency=null)
            =>new(angle,covered,dependency?.Reason,roads.ToArray()){Prefabs=usedPrefabs.ToArray(),MapIssue=dependency};
        RoadSlopeWindow Complete()
        {
            if(covered<NearZeroHorizontalMeters)
                return Result(null) with { CoveredMeters=0 };
            double angle=Math.Atan2(rise,covered)*180/Math.PI;
            if(!double.IsFinite(angle))
                throw new NotSupportedException($"Node {node:X16}: cannot determine a slope from {covered} m of geometry and {rise} m of rise.");
            return Result(angle);
        }
        if(covered>=WindowMeters)return Complete();
        var options=(incidentRoads.GetValueOrDefault(node)??[]).Where(r=>!roads.Contains(r.Uid)).ToArray();
#if DEBUG
        if(options.Length>1)
            throw new NavigationContractException($"Node {node:X16}: slope sampling found multiple unvisited roads ({string.Join(", ",options.Select(road=>road.Uid.ToString("X16")))}).");
#endif
        if(options.FirstOrDefault() is {} road)
        {
            roads.Add(road.Uid);
            if(!float.IsFinite(road.Length)||road.Length<0)
                throw new InvalidDataException($"Road {road.Uid:X16} at node {node:X16}: native length {road.Length} is invalid.");
            if(road.Length<NearZeroHorizontalMeters)return Complete();
            bool forward=road.Node.Uid==node;
            int steps=Math.Clamp((int)Math.Ceiling(road.Length/SampleStepMeters),4,100_000);
            Vector3 At(int i)=>road.InterpolateCurve(forward?(float)i/steps:1-(float)i/steps).Position;
            var previous=At(0);
            for(int i=1;i<=steps&&covered<WindowMeters;i++)
            {
                var point=At(i);
                if(!Add(previous,point,incoming,ref covered,ref rise))
                    throw new NotSupportedException($"Road {road.Uid:X16} at node {node:X16}: slope sample {i} contains nonfinite coordinates.");
                previous=point;
            }
            return Read(forward?road.ForwardNode.Uid:road.Node.Uid,incoming,excludePrefab,common,lane,covered,rise,roads,usedPrefabs);
        }

        var edges=(incoming?incomingPrefabs:outgoingPrefabs)[node].Where(e=>NativeLaneGraph.Prefab(e)!=excludePrefab&&!usedPrefabs.Contains(e.LanePaths.FirstOrDefault()?.PrefabUid??0))
            .Where(e=>e.LanePaths.Any(p=>lane is null||(incoming?p.ExitOrder:p.EntryOrder)==lane)).ToArray();
        if(edges.Length==0)
        {
            if(missingConnection?.Invoke(node) is MissingMapObjects missing)
                return Result(null,missing);
            return Complete();
        }
        if(edges.Any(e=>rampPassages.Contains((NativeLaneGraph.Prefab(e)!,e.StartNodeUid,e.EndNodeUid)))
            ||common&&edges.Select(e=>incoming?e.StartNodeUid:e.EndNodeUid).Distinct().Skip(1).Any())
            return Complete();
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
                    throw new NotSupportedException($"Prefab {path.PrefabUid:X16} at node {node:X16}: slope sample {i} on lane {path.EntryOrder}->{path.ExitOrder} contains nonfinite coordinates.");
            }
            windows.Add(Read(incoming?path.EntryNodeUid:path.ExitNodeUid,incoming,excludePrefab,common,
                incoming?path.EntryOrder:path.ExitOrder,distance,height,new(roads),visited));
        }
        var dependencies=windows.Select(w=>w.MapIssue).OfType<MissingMapObjects>().ToArray();
        if(dependencies.Length>0)
        {
            var first=windows.First(w=>w.MapIssue is MissingMapObjects);
            return first with { MapIssue=new MissingMapObjects(dependencies.SelectMany(d=>d.Keys),dependencies[0].Reason) };
        }
        var angles=windows.Select(w=>w.AngleDegrees).OfType<double>().ToArray();
        var inconsistent=windows.Any(w=>w.InconsistentLaneSlopes)
            ||angles.Length>1&&angles.Max()-angles.Min()>ParallelSlopeToleranceDegrees;
        double available=windows.Min(w=>w.CoveredMeters);
        return new(!inconsistent&&available>0?angles.Average():null,available,null,
            windows.SelectMany(w=>w.Roads).Distinct().ToArray())
            {Prefabs=windows.SelectMany(w=>w.Prefabs).Distinct().ToArray(),InconsistentLaneSlopes=inconsistent};
    }

    private static HashSet<(string Prefab,ulong Entry,ulong Exit)> RampPassages(IReadOnlyList<DirectedRoad> edges)
    {
        var result=new HashSet<(string,ulong,ulong)>();
        foreach(var prefab in edges.Where(e=>!e.ReturnsToEntry&&e.Junction?.IsJunction!=true)
            .GroupBy(NativeLaneGraph.Prefab).Where(group=>group.Key is not null))
        {
            var ports=prefab.SelectMany(e=>e.LaneConnections?.Select(c=>(Entry:c.EntryNode,Exit:c.ExitNode))
                ?? [(Entry:e.StartNodeUid,Exit:e.EndNodeUid)]).Distinct().ToArray();
            var splits=ports.GroupBy(p=>p.Entry).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
            var joins=ports.GroupBy(p=>p.Exit).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
            foreach(var (entry,exit) in ports)
                if(splits.Contains(entry)||joins.Contains(exit))
                    result.Add((prefab.Key!,entry,exit));
        }
        return result;
    }

    private static bool Add(Vector3 from,Vector3 to,bool incoming,ref double covered,ref double rise)
    {
        if(!Finite(from)||!Finite(to))return false;
        double dx=(double)to.X-from.X,dz=(double)to.Z-from.Z;
        double horizontal=Math.Sqrt(dx*dx+dz*dz);
        if(horizontal<NearZeroHorizontalMeters)return true;
        double take=Math.Min(horizontal,WindowMeters-covered);
        double dy=(double)to.Y-from.Y;
        rise+=(incoming?-dy:dy)*take/horizontal;covered+=take;
        return true;
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
