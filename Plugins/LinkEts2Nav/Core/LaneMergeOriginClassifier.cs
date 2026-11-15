namespace LinkEts2Nav.Core;

public sealed record LaneMergeOriginDecision(LaneMergeKind Kind,ulong? SourcePrefab,ulong? EndingEntrance,
    ulong? MainEntrance,double BackwardMeters,string Reason);

/// <summary>Finds the nearest native joining interface behind two lanes. No lamps, route, or player pose participate.</summary>
public sealed class LaneMergeOriginClassifier(NativeLaneGraph graph)
{
    private readonly IReadOnlyDictionary<ulong,DirectedRoad[]> roads=graph.ThroughTraffic.Edges.Where(e=>NativeLaneGraph.Prefab(e) is null)
        .GroupBy(e=>e.EndNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
    private readonly IReadOnlyDictionary<ulong,PrefabLanePath[]> lanes=graph.ThroughTraffic.Edges.SelectMany(e=>e.LanePaths)
        .DistinctBy(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane))
        .GroupBy(p=>p.ExitNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
    private readonly IReadOnlyDictionary<ulong,PrefabLanePath[]> prefabs=graph.ThroughTraffic.Edges.SelectMany(e=>e.LanePaths)
        .DistinctBy(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane))
        .GroupBy(p=>p.PrefabUid).ToDictionary(g=>g.Key,g=>g.ToArray());

    public LaneMergeOriginDecision Classify(PrefabLanePath ending,double endingPrefix,
        PrefabLanePath main,double mainPrefix)
    {
        double a=0,b=0,prefixA=endingPrefix,prefixB=mainPrefix;var usedRoads=new HashSet<string>(StringComparer.Ordinal);
        var visited=new HashSet<(ulong,ulong,int,ulong,int)>();
        LaneMergeOriginDecision Decision(LaneMergeKind kind,string reason,ulong? source=null,ulong? fromA=null,ulong? fromB=null)
            =>new(kind,source,fromA,fromB,Math.Max(a,b),reason);
        LaneMergeOriginDecision Missing(string reason)=>Decision(LaneMergeKind.Uncertain,reason);
        while(true)
        {
            if(!double.IsFinite(a)||!double.IsFinite(b)||a<0||b<0)return Missing("反向原生里程无效");
            if(Math.Max(a,b)>500)return Decision(LaneMergeKind.LaneReduction,"反向已覆盖500米且未遇可判汇入口，按同源处理");
            if(ending.PrefabUid!=main.PrefabUid)return Missing("两线尚未对应同一原生预制件，来源不能比较");
            var all=prefabs.GetValueOrDefault(ending.PrefabUid)??[];
            bool joining=all.Where(p=>p.ExitNodeUid==ending.ExitNodeUid||p.ExitNodeUid==main.ExitNodeUid)
                .GroupBy(p=>p.ExitNodeUid).Any(g=>g.Select(p=>p.EntryNodeUid).Distinct().Count()>1);
            if(joining)
            {
                bool same=ending.EntryNodeUid==main.EntryNodeUid;
                return Decision(same?LaneMergeKind.LaneReduction:LaneMergeKind.Ramp,
                    $"最近汇入口{ending.PrefabUid:X16}：两线来源入口{ending.EntryNodeUid:X16}/{main.EntryNodeUid:X16}，"+(same?"同源":"异源"),
                    ending.PrefabUid,ending.EntryNodeUid,main.EntryNodeUid);
            }
            if(ending.EntryNodeUid!=main.EntryNodeUid)return Missing("未确认共同汇入口之前，两线来源接口已不同");
            if(ending.EntryLane==main.EntryLane)
                return Decision(LaneMergeKind.LaneReduction,"原生连接已证明两线继承自同一入口车道",ending.PrefabUid,ending.EntryNodeUid,main.EntryNodeUid);
            if(!visited.Add((ending.PrefabUid,ending.EntryNodeUid,ending.EntryLane,main.EntryNodeUid,main.EntryLane)))
                return Decision(LaneMergeKind.LaneReduction,"原生来源闭环内无其他可判汇入口，按500米上限同源处理");

            a+=prefixA;b+=prefixB;
            if(Math.Max(a,b)>500)return Decision(LaneMergeKind.LaneReduction,"反向已覆盖500米且未遇可判汇入口，按同源处理");
            ulong boundary=ending.EntryNodeUid;
            while(true)
            {
                var previous=(roads.GetValueOrDefault(boundary)??[]).Where(e=>!usedRoads.Contains(e.Id)).ToArray();
                if(previous.Length==0)break;
                if(previous.Length!=1)return Missing("上游普通道路来源不唯一");
                var road=previous[0];double length=RouteGeometry.Length(road);
                if(!double.IsFinite(length)||length<=0)return Missing("上游普通道路长度无效");
                usedRoads.Add(road.Id);a+=length;b+=length;boundary=road.StartNodeUid;
                if(Math.Max(a,b)>500)return Decision(LaneMergeKind.LaneReduction,"反向已覆盖500米且未遇可判汇入口，按同源处理");
            }
            var source=(lanes.GetValueOrDefault(boundary)??[]).Where(p=>p.PrefabUid!=ending.PrefabUid).ToArray();
            var side=source.Where(p=>p.ExitOrder==ending.EntryOrder).ToArray();
            var kept=source.Where(p=>p.ExitOrder==main.EntryOrder).ToArray();
            if(side.Length==0||kept.Length==0)return Missing($"接口{boundary:X16}上游地图或原生车道继承未确认");
            var sourcePrefabs=side.Concat(kept).Select(p=>p.PrefabUid).Distinct().ToArray();
            if(sourcePrefabs.Length==1)
            {
                var scope=prefabs.GetValueOrDefault(sourcePrefabs[0])??[];
                bool join=scope.Where(p=>side.Any(a=>a.ExitNodeUid==p.ExitNodeUid)||kept.Any(b=>b.ExitNodeUid==p.ExitNodeUid))
                    .GroupBy(p=>p.ExitNodeUid).Any(g=>g.Select(p=>p.EntryNodeUid).Distinct().Count()>1);
                if(join&&side.Select(p=>p.EntryNodeUid).Distinct().Count()==1&&kept.Select(p=>p.EntryNodeUid).Distinct().Count()==1)
                {
                    // At the nearest joining interface, lane-slot ambiguity inside one approach
                    // does not make its source direction ambiguous.
                    ending=side[0];main=kept[0];prefixA=prefixB=0;continue;
                }
            }
            if(side.Select(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane)).Distinct().Count()!=1
                ||kept.Select(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane)).Distinct().Count()!=1)
                return Missing("至少一条车道的上游来源尚未唯一确认");
            ending=side[0];main=kept[0];
            prefixA=new LanePolyline(ending.Points).Length;prefixB=new LanePolyline(main.Points).Length;
        }
    }
}
