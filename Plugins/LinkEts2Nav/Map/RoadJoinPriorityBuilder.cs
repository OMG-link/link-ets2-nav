using LinkEts2Nav.Core;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Annotates the already-loaded prefab snapshot; never opens archives or queries the full map.</summary>
public sealed class RoadJoinPriorityBuilder(IReadOnlyDictionary<ulong,List<Road>> incidentRoads,Action<string>? log=null,
    Func<ulong,MapQueryIssue>? missingConnection=null,Action<MapQueryIssue>? reportMapIssue=null)
{
    public IReadOnlyList<DirectedRoad> Annotate(IEnumerable<DirectedRoad> source)
    {
        var result=source.ToArray();
#if DEBUG
        var duplicate = result.Where(edge => NativeLaneGraph.Prefab(edge) is not null)
            .GroupBy(edge => (Prefab: NativeLaneGraph.Prefab(edge), edge.StartNodeUid, edge.EndNodeUid))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new NavigationContractException($"Prefab {duplicate.Key.Prefab}: duplicate passage {duplicate.Key.StartNodeUid:X16}->{duplicate.Key.EndNodeUid:X16}.");
#endif
        var sampler=new RoadSlopeWindowSampler(incidentRoads,result.Where(e=>!e.ReturnsToEntry).ToArray(),missingConnection);
        var passages=result.Select((edge,index)=>(edge,index)).Where(p=>!p.edge.ReturnsToEntry&&p.edge.Id.StartsWith("prefab:",StringComparison.Ordinal)
            &&p.edge.Junction?.IsJunction!=true).ToArray();
        var groups=passages
            .GroupBy(p=>(Prefab:p.edge.Id.Split(':')[1],Exit:p.edge.EndNodeUid));
        foreach(var group in groups)
        {
            var entries=group.SelectMany(p=>Connections(p.edge)).Where(p=>p.ExitNode==group.Key.Exit).GroupBy(p=>p.EntryNode).ToArray();
            if(entries.Length<2)continue;
            var output=sampler.Sample(group.Key.Exit,false,group.Key.Prefab,common:true);var measurements=new List<RoadJoinEntryMeasurement>();
            var dependencies=new List<MissingMapObjects>();
            if(output.MapIssue is {} outputIssue){dependencies.Add(outputIssue);reportMapIssue?.Invoke(outputIssue);}
            foreach(var entry in entries)
            {
                ulong uid=entry.Key;int count=entry.Select(c=>c.EntryLane).Distinct().Count();
                var candidate=group.Where(p=>p.edge.StartNodeUid==uid).Select(p=>p.edge).FirstOrDefault();
                var input=sampler.Sample(uid,true,group.Key.Prefab);
                if(input.MapIssue is {} inputIssue){dependencies.Add(inputIssue);reportMapIssue?.Invoke(inputIssue);}
                double? angle=candidate is null?null:DirectionDifference(candidate);
                string? failure=angle is null?"竞争入口原生方向缺失":null;
                measurements.Add(new(uid,count,input.AngleDegrees,output.AngleDegrees,angle,failure)
                    {SlopeWeightFactor=SlopeWeightFactor(input,output)});
            }
            var (priority,dependency)=Score(measurements,dependencies);
            foreach(var (_,index) in group)
                result[index]=result[index] with{JoinPriority=priority,JoinPriorityDependency=dependency};
            if(priority is null)continue;
            log?.Invoke($"组件入口主路评分：prefab:{group.Key.Prefab}，出口={group.Key.Exit:X16}，主路={priority.MainEntry?.ToString("X16")??"未知"}；{priority.Reason}；"+
                string.Join("；",priority.Scores.Select(s=>FormattableString.Invariant($"{s.Measurement.Entry:X16} 坡差={s.Measurement.SlopeDifferenceDegrees:F4}° 角差={s.Measurement.DirectionDifferenceDegrees:F4}° 车道={s.Measurement.LaneCount}，分数={s.SlopeScore:F3}+{s.DirectionScore:F3}+{s.LaneScore:F3}={s.Total:F3}"))));
        }
        var continuity=new Dictionary<NativeLaneGeometry,LaneContinuityResult>(ReferenceEqualityComparer.Instance);
        bool ProvenHard(PrefabLanePath path)
        {
            if(path.NativeGeometry is not {} geometry)return false;
            if(!continuity.TryGetValue(geometry,out var facts))
                continuity[geometry]=facts=LaneContinuity.Analyze(geometry);
            return facts.MapIssue is null&&facts.FirstSideSplit(path.CurveIndices) is { } split
                &&split.Point.MapIssue is null&&split.Branch is {Role:LaneContinuityRole.Newborn,MapIssue:null};
        }
        foreach(var group in passages.GroupBy(p=>(Prefab:p.edge.Id.Split(':')[1],Entry:p.edge.StartNodeUid)))
        {
            var outlets=group.SelectMany(p=>Connections(p.edge)).Where(p=>p.EntryNode==group.Key.Entry).GroupBy(p=>p.ExitNode).ToArray();
            if(outlets.Length<2)continue;
            var byOutlet=group.ToLookup(p=>p.edge.EndNodeUid,p=>p.edge);
            bool IsHardOutlet(IGrouping<ulong,LaneConnection> outlet)
            {
                var candidate=byOutlet[outlet.Key].FirstOrDefault();
                if(candidate is null||candidate.LanePaths.Count==0)return false;
                var paths=candidate.LanePaths;
                var ports=paths.Select(p=>(p.EntryLane,p.ExitLane)).ToHashSet();
                return outlet.All(c=>ports.Contains((c.EntryLane,c.ExitLane)))&&paths.All(ProvenHard);
            }
            var eligible=outlets.Where(outlet=>!IsHardOutlet(outlet)).ToArray();
            (RoadJoinPriority? priority,MissingMapObjects? dependency)=eligible.Length switch
            {
                0=>(new RoadJoinPriority(null,[],[],null,"All native passages from this entrance are hard outlets."){NoEligibleOutlet=true},null),
                1=>(SelectSoleOutlet(eligible[0].Key),null),
                _=>ScoreOutlets()
            };
            foreach(var (_,index) in group)
                result[index]=result[index] with{SplitPriority=priority,SplitPriorityDependency=dependency};
            if(priority is null)continue;
            log?.Invoke($"组件出口主路评分：prefab:{group.Key.Prefab}，入口={group.Key.Entry:X16}，主路={priority.MainEntry?.ToString("X16")??"未知"}；"+
                $"硬出口排除={outlets.Length-eligible.Length}；{priority.Reason}；"+
                string.Join("；",priority.Scores.Select(s=>FormattableString.Invariant($"{s.Measurement.Entry:X16} 坡差={s.Measurement.SlopeDifferenceDegrees:F4}° 角差={s.Measurement.DirectionDifferenceDegrees:F4}° 车道={s.Measurement.LaneCount}，分数={s.Total:F3}"))));

            RoadJoinPriority SelectSoleOutlet(ulong outlet)
            {
                if(byOutlet[outlet].FirstOrDefault() is not {LanePaths.Count:>0})
                    throw new NotSupportedException($"Prefab {group.Key.Prefab}, entrance {group.Key.Entry:X16}: the sole outlet {outlet:X16} after hard-outlet exclusion has no native passage with lane geometry.");
                return new(outlet,[],[],null,"Only one candidate remains after excluding hard outlets.");
            }

            (RoadJoinPriority? Priority,MissingMapObjects? Dependency) ScoreOutlets()
            {
                var input=sampler.Sample(group.Key.Entry,true,group.Key.Prefab,common:true);
                var measurements=new List<RoadJoinEntryMeasurement>();
                var dependencies=new List<MissingMapObjects>();
                if(input.MapIssue is {} inputIssue){dependencies.Add(inputIssue);reportMapIssue?.Invoke(inputIssue);}
                foreach(var outlet in eligible)
                {
                    var candidate=byOutlet[outlet.Key].FirstOrDefault();
                    var output=sampler.Sample(outlet.Key,false,group.Key.Prefab);
                    if(output.MapIssue is {} issue){dependencies.Add(issue);reportMapIssue?.Invoke(issue);}
                    double? angle=candidate is null?null:DirectionDifference(candidate);
                    string? failure=angle is null?"竞争出口原生方向缺失":null;
                    measurements.Add(new(outlet.Key,outlet.Select(p=>p.ExitLane).Distinct().Count(),
                        output.AngleDegrees is {} a?-a:null,input.AngleDegrees is {} b?-b:null,angle,failure)
                        {SlopeWeightFactor=SlopeWeightFactor(output,input)});
                }
                return Score(measurements,dependencies);
            }
        }
        return result;
    }

    private static double SlopeWeightFactor(RoadSlopeWindow candidate,RoadSlopeWindow common)
        =>candidate.InconsistentLaneSlopes||common.InconsistentLaneSlopes||common.CoveredMeters==0
            ?0:candidate.CoveredMeters/RoadSlopeWindowSampler.WindowMeters;

    private static (RoadJoinPriority? Priority,MissingMapObjects? Dependency) Score(
        IReadOnlyList<RoadJoinEntryMeasurement> measurements,IReadOnlyList<MissingMapObjects> dependencies)
    {
        if(dependencies.Count>0)
            return (null,new MissingMapObjects(dependencies.SelectMany(d=>d.Keys),
                "Component slope scoring needs native lookup keys."){QueryAnchor=dependencies[0].QueryAnchor});
        return (RoadJoinPriorityScorer.Evaluate(measurements),null);
    }

    private static IEnumerable<LaneConnection> Connections(DirectedRoad edge)=>edge.LaneConnections
        ??edge.LanePaths.Select(p=>new LaneConnection(p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane)).ToArray();

    private static double DirectionDifference(DirectedRoad edge)
    {
#if DEBUG
        if (edge.NativeEntryTangent is null || edge.NativeExitTangent is null)
            throw new NavigationContractException($"Prefab passage {edge.Id}: direction scoring requires native entry and exit tangents.");
#endif
        var entry = edge.NativeEntryTangent!.Value;
        var exit = edge.NativeExitTangent!.Value;
        double cross = (double)entry.X * exit.Z - (double)entry.Z * exit.X;
        double dot = (double)entry.X * exit.X + (double)entry.Z * exit.Z;
        return Math.Abs(Math.Atan2(cross, dot)) * 180 / Math.PI;
    }
}
