using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

public sealed record LaneMergeSourceStep(ulong PrefabUid,ulong EndingEntrance,int EndingLane,
    ulong MainEntrance,int MainLane,double BackwardMeters,IReadOnlyList<int> ApplicableEntryLanes);
public sealed record LaneMergeOriginDecision(LaneMergeKind Kind,ulong? SourcePrefab,ulong? EndingEntrance,
    ulong? MainEntrance,double BackwardMeters,string Reason)
{
    public MapQueryIssue? MapIssue {get;init;}
    public ulong? ApplicableEntryNode {get;init;}
    public ulong? ApplicabilitySourcePrefab {get;init;}
    public ulong? SourceEntrance {get;init;}
    public IReadOnlyList<int> ApplicableEntryLanes {get;init;}=[];
    public IReadOnlyList<LaneMergeSourceStep> Steps {get;init;}=[];
}

/// <summary>Follows a native lane pair through same-input structures within one total 500 m source window.</summary>
public sealed class LaneMergeOriginClassifier
{
    private readonly Func<ulong,MapQueryIssue>? missingConnection;
    private readonly IReadOnlyDictionary<ulong,DirectedRoad[]> roads;
    private readonly IReadOnlyDictionary<ulong,PrefabLanePath[]> lanes;
    private readonly IReadOnlyDictionary<(ulong Prefab,ulong Entry),PrefabLanePath[]> entries;
    private readonly IReadOnlySet<(ulong Prefab,ulong Exit)> joins;
    public LaneMergeOriginClassifier(NativeLaneGraph graph,Func<ulong,MapQueryIssue>? missingConnection=null)
    {
        this.missingConnection=missingConnection;
        var native=graph.ThroughTraffic.Edges;
        roads=native.Where(e=>NativeLaneGraph.Prefab(e) is null).GroupBy(e=>e.EndNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        var paths=native.SelectMany(e=>e.LanePaths)
            .DistinctBy(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane,
                Curves:string.Join(',',p.CurveIndices),Length:new LanePolyline(p.Points).Length)).ToArray();
        lanes=paths.GroupBy(p=>p.ExitNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        entries=paths.GroupBy(p=>(p.PrefabUid,p.EntryNodeUid)).ToDictionary(g=>g.Key,g=>g.ToArray());
        joins=paths.GroupBy(p=>(p.PrefabUid,p.ExitNodeUid)).Where(g=>g.Select(p=>p.EntryNodeUid).Distinct().Count()>1)
            .Select(g=>g.Key).ToHashSet();
    }

    public LaneMergeOriginDecision Classify(PrefabLanePath ending,double endingPrefix,PrefabLanePath main,double mainPrefix)
    {
        double a=0,b=0,aMin=0,bMin=0,prefixA=endingPrefix,prefixB=mainPrefix,prefixMinA=endingPrefix,prefixMinB=mainPrefix;
        ulong? approach=ending.EntryNodeUid==main.EntryNodeUid?ending.EntryNodeUid:null,sourceEntrance=null,sourcePrefab=null;
        var orders=(entries.GetValueOrDefault((ending.PrefabUid,ending.EntryNodeUid))??[])
            .GroupBy(p=>p.EntryLane).Where(g=>g.Select(p=>p.EntryOrder).Distinct().Count()==1)
            .ToDictionary(g=>g.Key,g=>g.First().EntryOrder);
        Dictionary<int,PrefabLanePath[]>? projection=null;
        IReadOnlyList<int>? applicable=null;
        var steps=new List<LaneMergeSourceStep>();
        MapQueryIssue? mapIssue=null;
        var visited=new Dictionary<(ulong,ulong,int,ulong,int,string),(double A,double B)>();
        LaneMergeOriginDecision Decision(LaneMergeKind kind,string reason,ulong? source=null,ulong? fromA=null,ulong? fromB=null)
            =>new(kind,source,fromA,fromB,Math.Min(500,Math.Max(a,b)),reason)
            {ApplicableEntryNode=approach,ApplicabilitySourcePrefab=sourcePrefab,SourceEntrance=sourceEntrance??approach,
                ApplicableEntryLanes=applicable??Array.AsReadOnly(orders.Keys.Order().ToArray()),Steps=Array.AsReadOnly(steps.ToArray()),MapIssue=mapIssue};
        LaneMergeOriginDecision Missing(string reason,MapQueryIssue? issue=null)
        {
            mapIssue=issue??new MapQueryFailure(MapQueryFailureKind.Ambiguous,reason);
            return Decision(LaneMergeKind.Uncertain,reason);
        }
        LaneMergeOriginDecision Window()=>Decision(LaneMergeKind.LaneReduction,"原生来源累计已覆盖500米，窗口内未发现两线分属不同入口");
        LaneMergeOriginDecision? Budget()=>Math.Max(a,b)<=500?null:Math.Max(aMin,bMin)<=500
            ?Missing("原生曲线候选的累计里程跨越500米边界，来源未唯一确认"):Window();
        while(true)
        {
            if(!double.IsFinite(prefixA)||!double.IsFinite(prefixB)||!double.IsFinite(prefixMinA)||!double.IsFinite(prefixMinB)
                ||prefixMinA<0||prefixMinB<0)return Missing("反向原生里程无效");
            a+=prefixA;b+=prefixB;aMin+=prefixMinA;bMin+=prefixMinB;
            if(Budget() is {} exhausted)return exhausted;
            if(ending.PrefabUid!=main.PrefabUid)return Missing("两线未对应同一原生来源结构");
            if(ending.EntryNodeUid!=main.EntryNodeUid)
            {
                steps.Add(new(ending.PrefabUid,ending.EntryNodeUid,ending.EntryLane,main.EntryNodeUid,main.EntryLane,
                    Math.Max(a,b),applicable??Array.AsReadOnly(orders.Keys.Order().ToArray())));
                return Decision(LaneMergeKind.Ramp,$"500米内两线来源入口不同：{ending.EntryNodeUid:X16}/{main.EntryNodeUid:X16}",
                    ending.PrefabUid,ending.EntryNodeUid,main.EntryNodeUid);
            }

            if(projection is not null)
                orders=projection.Where(p=>p.Value.Length>0&&p.Value.All(v=>v.EntryNodeUid==ending.EntryNodeUid)
                    &&p.Value.Select(v=>v.EntryOrder).Distinct().Count()==1)
                    .ToDictionary(p=>p.Key,p=>p.Value[0].EntryOrder);
            // A identifies the nearest joining approach's original downstream lane group.
            // Older recursive sources affect classification, not this already established applicability.
            if(applicable is null&&joins.Contains((ending.PrefabUid,ending.ExitNodeUid)))
            {
                sourceEntrance=ending.EntryNodeUid;sourcePrefab=ending.PrefabUid;
                applicable=Array.AsReadOnly(orders.Keys.Order().ToArray());
            }
            steps.Add(new(ending.PrefabUid,ending.EntryNodeUid,ending.EntryLane,main.EntryNodeUid,main.EntryLane,
                Math.Max(a,b),applicable??Array.AsReadOnly(orders.Keys.Order().ToArray())));
            if(Math.Max(aMin,bMin)>=500)return Window();
            string mapping=string.Join(';',orders.OrderBy(p=>p.Key).Select(p=>$"{p.Key}:{p.Value}"));
            var key=(ending.PrefabUid,ending.EntryNodeUid,ending.EntryLane,main.EntryNodeUid,main.EntryLane,mapping);
            if(visited.TryGetValue(key,out var prior))
            {
                if(aMin<=prior.A||bMin<=prior.B)return Missing("原生来源闭环未提供正向里程");
                a=b=500;return Window(); // A fully known positive deterministic cycle adds no new source.
            }
            visited[key]=(aMin,bMin);
            ulong boundary=ending.EntryNodeUid;
            var visitedRoads=new HashSet<ulong>();
            string? lastRoad=null;
            while(true)
            {
                var previous=(roads.GetValueOrDefault(boundary)??[]).Where(e=>e.Id!=lastRoad).ToArray();
                if(previous.Length==0)break;
                if(previous.Length!=1)return Missing("上游普通道路来源不唯一");
                var road=previous[0];double length=RouteGeometry.Length(road);
                if(!double.IsFinite(length)||length<=0)return Missing("上游普通道路长度无效");
                if(!visitedRoads.Add(boundary)){a=b=500;return Window();}
                a+=length;b+=length;aMin+=length;bMin+=length;boundary=road.StartNodeUid;lastRoad=road.Id;
                if(Budget() is {} roadEnd)return roadEnd;
            }
            var source=(lanes.GetValueOrDefault(boundary)??[]).Where(p=>p.PrefabUid!=ending.PrefabUid).ToArray();
            var side=source.Where(p=>p.ExitOrder==ending.EntryOrder&&p.ExitLaneCount==ending.EntryLaneCount).ToArray();
            var kept=source.Where(p=>p.ExitOrder==main.EntryOrder&&p.ExitLaneCount==main.EntryLaneCount).ToArray();
            if(side.Length==0||kept.Length==0)
            {
                string reason=$"接口{boundary:X16}上游地图或原生车道继承未确认";
                return Missing(reason,missingConnection?.Invoke(boundary)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,reason));
            }
            if(side.Concat(kept).Select(p=>p.PrefabUid).Distinct().Count()!=1)return Missing("两线的上游预制件来源不唯一");
            var fromA=side.Select(p=>p.EntryNodeUid).Distinct().ToArray();var fromB=kept.Select(p=>p.EntryNodeUid).Distinct().ToArray();
            if(fromA.Length!=1||fromB.Length!=1)return Missing("至少一条车道的上游来源入口尚未唯一确认");
            // Different entrances are decisive after the known native prefixes fit the total budget.
            if(fromA[0]==fromB[0]&&(side.Select(p=>p.EntryLane).Distinct().Count()!=1||kept.Select(p=>p.EntryLane).Distinct().Count()!=1))
                return Missing("同一来源入口内的上游车道槽尚未唯一确认");
            var sideLengths=side.Select(p=>new LanePolyline(p.Points).Length).ToArray();
            var keptLengths=kept.Select(p=>new LanePolyline(p.Points).Length).ToArray();
            projection=orders.ToDictionary(p=>p.Key,p=>source.Where(v=>v.ExitOrder==p.Value&&v.ExitLaneCount==ending.EntryLaneCount).ToArray());
            ending=side[0];main=kept[0];
            prefixA=sideLengths.Max();prefixB=keptLengths.Max();
            prefixMinA=sideLengths.Min();prefixMinB=keptLengths.Min();
        }
    }
}
