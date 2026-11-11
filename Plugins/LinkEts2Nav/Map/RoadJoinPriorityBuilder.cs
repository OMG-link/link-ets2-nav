using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Annotates the already-loaded prefab snapshot; never opens archives or queries the full map.</summary>
public sealed class RoadJoinPriorityBuilder(IReadOnlyDictionary<ulong,List<Road>> incidentRoads,Action<string>? log=null)
{
    public IReadOnlyList<DirectedRoad> Annotate(IEnumerable<DirectedRoad> source)
    {
        var result=source.ToArray();var sampler=new RoadSlopeWindowSampler(incidentRoads);
        var groups=result.Select((edge,index)=>(edge,index)).Where(p=>p.edge.Id.StartsWith("prefab:",StringComparison.Ordinal))
            .GroupBy(p=>(Prefab:p.edge.Id.Split(':')[1],Exit:p.edge.EndNodeUid));
        foreach(var group in groups)
        {
            var entries=RoadJoinTopology.IndependentEntries(group.First().edge.LaneConnections,group.Key.Exit);
            if(entries.Count==0)continue;
            var output=sampler.Sample(group.Key.Exit,false);var measurements=new List<RoadJoinEntryMeasurement>();
            foreach(var entry in entries)
            {
                ulong uid=entry[0].EntryNode;int count=entry.Select(c=>c.EntryLane).Distinct().Count();
                var candidates=group.Where(p=>p.edge.StartNodeUid==uid).Select(p=>p.edge).ToArray();
                var input=sampler.Sample(uid,true);
                double? angle=candidates.Length==1?DirectionDifference(candidates[0]):null;
                string? failure=input.Failure??output.Failure;
                if(angle is null)failure=(failure is null?"":failure+"；")+"竞争入口原生方向缺失或不唯一";
                measurements.Add(new(uid,count,input.AngleDegrees,output.AngleDegrees,angle,failure));
            }
            var priority=RoadJoinPriorityScorer.Evaluate(measurements.ToArray());
            foreach(var (edge,index) in group)result[index]=edge with{JoinPriority=priority};
            log?.Invoke($"无冲突汇合主路评分：prefab:{group.Key.Prefab}，出口={group.Key.Exit:X16}，主路={priority.MainEntry?.ToString("X16")??"未知"}；{priority.Reason}；"+
                string.Join("；",priority.Scores.Select(s=>FormattableString.Invariant($"{s.Measurement.Entry:X16} 坡差={s.Measurement.SlopeDifferenceDegrees:F4}° 角差={s.Measurement.DirectionDifferenceDegrees:F4}° 车道={s.Measurement.LaneCount}，分数={s.SlopeScore:F3}+{s.DirectionScore:F3}+{s.LaneScore:F3}={s.Total:F3}"))));
        }
        return result;
    }

    private static double? DirectionDifference(DirectedRoad edge)
    {
        if(edge.NativeEntryTangent is not {} a||edge.NativeExitTangent is not {} b)return null;
        var x=new Vector2(a.X,a.Z);var y=new Vector2(b.X,b.Z);
        if(!float.IsFinite(x.X)||!float.IsFinite(x.Y)||!float.IsFinite(y.X)||!float.IsFinite(y.Y)
            ||x.LengthSquared()<.000001||y.LengthSquared()<.000001)return null;
        return Math.Abs(Math.Atan2((double)x.X*y.Y-(double)x.Y*y.X,Vector2.Dot(x,y)))*180/Math.PI;
    }
}
