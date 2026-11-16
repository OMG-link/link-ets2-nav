using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

public sealed record LaneMergeSourceStep(ulong PrefabUid,ulong EndingEntrance,int EndingLane,
    ulong MainEntrance,int MainLane,double BackwardMeters,IReadOnlyList<int> ApplicableEntryLanes);
public sealed record LaneMergeSourceBirth(ulong PrefabUid,int SharedCurve,int BranchCurve,int ExitOrder,double BackwardMeters);
public sealed record LaneMergeOriginDecision(LaneMergeKind Kind,ulong? SourcePrefab,ulong? EndingEntrance,
    ulong? MainEntrance,double BackwardMeters,string Reason)
{
    public MapQueryIssue? MapIssue {get;init;}
    public ulong? ApplicableEntryNode {get;init;}
    public ulong? ApplicabilitySourcePrefab {get;init;}
    public ulong? SourceEntrance {get;init;}
    public IReadOnlyList<int> ApplicableEntryLanes {get;init;}=[];
    public IReadOnlyList<LaneMergeSourceStep> Steps {get;init;}=[];
    public IReadOnlyList<LaneMergeSourceBirth> Births {get;init;}=[];
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

    private readonly Dictionary<NativeLaneGeometry,LaneContinuityResult> continuity=new(ReferenceEqualityComparer.Instance);
    private enum Identity { Continues, Dead, Unknown }
    private (Identity Kind,MapQueryIssue? Issue,IReadOnlyList<LaneMergeSourceBirth> Births) IdentityOf(PrefabLanePath path,double usedMin,double usedMax)
    {
        (Identity,MapQueryIssue?,IReadOnlyList<LaneMergeSourceBirth>) Unknown(string reason,MapQueryFailureKind kind=MapQueryFailureKind.DefinitionUnavailable)
            =>(Identity.Unknown,new MapQueryFailure(kind,reason),[]);
        if(path.NativeGeometry is not {} geometry)return Unknown("Upstream lane lacks native continuity geometry.");
        if(!continuity.TryGetValue(geometry,out var result))continuity[geometry]=result=LaneContinuity.Analyze(geometry);
        if(geometry.Curves.Count==0||geometry.Curves.GroupBy(c=>c.Index).Any(g=>g.Count()!=1))
            return (Identity.Unknown,result.MapIssue??new MapQueryFailure(MapQueryFailureKind.InvalidData,"Native curve index is not usable."),[]);
        if(path.CurveIndices.Count==0||path.CurveIndices.Any(i=>!geometry.Curves.Any(c=>c.Index==i)))
            return Unknown("Upstream lane references unavailable native curves.");
        var births=new List<LaneMergeSourceBirth>();bool dead=false;
        var line=new LanePolyline(path.Points);
        foreach(var curve in geometry.Curves.Where(c=>path.CurveIndices.Contains(c.Index)&&c.MapIssue is not null))
        {
            int slot=path.CurveIndices.ToList().IndexOf(curve.Index);
            if(path.CurveStartPointIndices.Count!=path.CurveIndices.Count)return (Identity.Unknown,curve.MapIssue,[]);
            int end=slot+1<path.CurveIndices.Count?path.CurveStartPointIndices[slot+1]:path.Points.Count-1;
            if(end<0||end>=path.Points.Count)return (Identity.Unknown,curve.MapIssue,[]);
            double suffix=line.Length-path.Points.Take(end+1).Zip(path.Points.Skip(1).Take(end)).Sum(p=>(double)System.Numerics.Vector3.Distance(p.First,p.Second));
            if(usedMin+suffix<=500)return (Identity.Unknown,curve.MapIssue,[]);
        }
        for(int i=0;i<path.CurveIndices.Count;i++)
        foreach(var point in result.Points.Where(p=>p.SharedCurveIndex==path.CurveIndices[i]))
        {
            int at=point.Kind==LaneContinuityPointKind.Merge?i:i+1;
            int branch=point.Kind==LaneContinuityPointKind.Merge?(i>0?path.CurveIndices[i-1]:-1)
                :(i+1<path.CurveIndices.Count?path.CurveIndices[i+1]:-1);
            if(branch<0)continue;
            if(path.CurveStartPointIndices.Count!=path.CurveIndices.Count||at>=path.CurveStartPointIndices.Count)
                return Unknown("Upstream native event lacks its measured path boundary.");
            int index=path.CurveStartPointIndices[at];
            if(index<0||index>=path.Points.Count)return Unknown("Upstream native event path boundary is invalid.",MapQueryFailureKind.InvalidData);
            double prefix=path.Points.Take(index+1).Zip(path.Points.Skip(1).Take(index)).Sum(p=>(double)System.Numerics.Vector3.Distance(p.First,p.Second));
            double suffix=line.Length-prefix;
            // Qualification outside this query's 500m window cannot change its source.
            if(usedMin+suffix>500)continue;
            if(usedMax+suffix>500)return Unknown("Native lane event spans the cumulative 500m source boundary.",MapQueryFailureKind.Ambiguous);
            var role=point.Branches.SingleOrDefault(b=>b.CurveIndex==branch)?.Role;
            if(point.MapIssue is not null||role is null or LaneContinuityRole.Unknown)
                return Unknown("Upstream native lane has no confirmed surviving source.",MapQueryFailureKind.Ambiguous);
            if(point.Kind==LaneContinuityPointKind.Merge)dead|=role==LaneContinuityRole.Dead;
            // A newborn loses main-lane qualification, not its known common input source.
            else if(role==LaneContinuityRole.Newborn)births.Add(new(path.PrefabUid,point.SharedCurveIndex,branch,path.ExitOrder,usedMax+suffix));
        }
        return (dead?Identity.Dead:Identity.Continues,null,births);
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
        var steps=new List<LaneMergeSourceStep>();var births=new List<LaneMergeSourceBirth>();
        var seedLine=ending.Points.Count>1?new LanePolyline(ending.Points):null;
        System.Numerics.Vector3? queryAnchor=seedLine is not null&&double.IsFinite(endingPrefix)
            ?seedLine.At(Math.Clamp(endingPrefix,0,seedLine.Length)):null;
        MapQueryIssue? mapIssue=null;
        var visited=new Dictionary<(ulong,ulong,int,ulong,int,string),(double A,double B)>();
        LaneMergeOriginDecision Decision(LaneMergeKind kind,string reason,ulong? source=null,ulong? fromA=null,ulong? fromB=null)
            =>new(kind,source,fromA,fromB,Math.Min(500,Math.Max(a,b)),reason)
            {ApplicableEntryNode=approach,ApplicabilitySourcePrefab=sourcePrefab,SourceEntrance=sourceEntrance??approach,
                ApplicableEntryLanes=applicable??Array.AsReadOnly(orders.Keys.Order().ToArray()),Steps=Array.AsReadOnly(steps.ToArray()),Births=Array.AsReadOnly(births.Distinct().ToArray()),MapIssue=mapIssue};
        LaneMergeOriginDecision Missing(string reason,MapQueryIssue? issue=null)
        {
            mapIssue=(issue??new MapQueryFailure(MapQueryFailureKind.Ambiguous,reason)) with{QueryAnchor=queryAnchor};
            return Decision(LaneMergeKind.Uncertain,reason);
        }
        LaneMergeOriginDecision Window()=>Decision(LaneMergeKind.LaneReduction,"原生来源累计已覆盖500米，窗口内未发现两线分属不同入口");
        LaneMergeOriginDecision? Budget()=>Math.Max(a,b)<=500?null:Math.Max(aMin,bMin)<=500
            ?Missing("原生曲线候选的累计里程跨越500米边界，来源未唯一确认"):Window();
        if(ending.EntryNodeUid==main.EntryNodeUid
            &&(!orders.Values.Contains(ending.EntryOrder)||!orders.Values.Contains(main.EntryOrder)))
            return Missing("Queried native lane mapping is unavailable; no earlier source is assumed.");
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
            if(!orders.Values.Contains(ending.EntryOrder)||!orders.Values.Contains(main.EntryOrder))
                return Missing("Queried native lane mapping is unavailable; no earlier source is assumed.");
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
            if(!source.Any(p=>p.ExitOrder==ending.EntryOrder&&p.ExitLaneCount==ending.EntryLaneCount)
                ||!source.Any(p=>p.ExitOrder==main.EntryOrder&&p.ExitLaneCount==main.EntryLaneCount))
            {
                string reason=$"接口{boundary:X16}上游地图或原生车道继承未确认";
                return Missing(reason,missingConnection?.Invoke(boundary)
                    ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,reason));
            }
            var relevant=source.Where(p=>p.ExitLaneCount==ending.EntryLaneCount&&orders.Values.Contains(p.ExitOrder))
                .Select(p=>(Path:p,Evidence:IdentityOf(p,
                    p.ExitOrder==ending.EntryOrder?aMin:p.ExitOrder==main.EntryOrder?bMin:Math.Min(aMin,bMin),
                    p.ExitOrder==ending.EntryOrder?a:p.ExitOrder==main.EntryOrder?b:Math.Max(a,b)))).ToArray();
            if(relevant.FirstOrDefault(p=>p.Evidence.Kind==Identity.Unknown
                &&(p.Path.ExitOrder==ending.EntryOrder||p.Path.ExitOrder==main.EntryOrder)) is var incomplete&&incomplete.Path is not null)
                return Missing("Upstream lane identity cannot be established from native continuity.",incomplete.Evidence.Issue);
            births.AddRange(relevant.Where(p=>p.Evidence.Kind==Identity.Continues).SelectMany(p=>p.Evidence.Births));
            source=relevant.Where(p=>p.Evidence.Kind==Identity.Continues).Select(p=>p.Path).ToArray();
            var side=source.Where(p=>p.ExitOrder==ending.EntryOrder&&p.ExitLaneCount==ending.EntryLaneCount).ToArray();
            var kept=source.Where(p=>p.ExitOrder==main.EntryOrder&&p.ExitLaneCount==main.EntryLaneCount).ToArray();
            if(side.Length==0||kept.Length==0)
                return Missing("Upstream physical predecessors have no surviving native lane identity.");
            var sideLengths=side.Select(p=>new LanePolyline(p.Points).Length).ToArray();
            var keptLengths=kept.Select(p=>new LanePolyline(p.Points).Length).ToArray();
            double nextMax=Math.Max(a+sideLengths.Max(),b+keptLengths.Max());
            double nextMin=Math.Max(aMin+sideLengths.Min(),bMin+keptLengths.Min());
            if(nextMax>500)
            {
                if(nextMin<=500)return Missing("Native path candidates span the cumulative 500m source boundary.");
                a=b=500;return Window();
            }
            if(side.Concat(kept).Select(p=>p.PrefabUid).Distinct().Count()!=1)return Missing("两线的上游预制件来源不唯一");
            var fromA=side.Select(p=>p.EntryNodeUid).Distinct().ToArray();var fromB=kept.Select(p=>p.EntryNodeUid).Distinct().ToArray();
            if(fromA.Length!=1||fromB.Length!=1)return Missing("至少一条车道的上游来源入口尚未唯一确认");
            // Different entrances are decisive after the known native prefixes fit the total budget.
            if(fromA[0]==fromB[0]&&(side.Select(p=>p.EntryLane).Distinct().Count()!=1||kept.Select(p=>p.EntryLane).Distinct().Count()!=1))
                return Missing("同一来源入口内的上游车道槽尚未唯一确认");
            projection=orders.ToDictionary(p=>p.Key,p=>source.Where(v=>v.ExitOrder==p.Value&&v.ExitLaneCount==ending.EntryLaneCount).ToArray());
            ending=side[0];main=kept[0];
            prefixA=sideLengths.Max();prefixB=keptLengths.Max();
            prefixMinA=sideLengths.Min();prefixMinB=keptLengths.Min();
        }
    }
}
