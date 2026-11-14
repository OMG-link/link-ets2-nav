using System.Collections.Frozen;
namespace LinkEts2Nav.Core;

public enum AuxiliaryBoundaryKind { IndependentEntrance, NativeSplit, NativeMerge, SeparateExit }
public sealed record NativeRoadKey(string Id,ulong StartNodeUid,ulong EndNodeUid)
{
    public static NativeRoadKey Of(DirectedRoad edge)=>new(edge.Id,edge.StartNodeUid,edge.EndNodeUid);
}
/// <summary>References existing directed geometry and local arc coordinates, never player-relative distances.</summary>
public sealed record AuxiliaryLaneSection(DirectedRoad Edge,double StartMeters,double EndMeters,
    double CorridorAlong,IReadOnlyList<int> SideOrders,IReadOnlyList<int> MainOrders);
public sealed record AuxiliaryLaneTerminal(DirectedRoad Edge,double EndMeters,bool SideOnly,bool MainOnly,bool Merges=false)
{
    public IReadOnlyList<int> SideOrders {get;init;}=[];
    public IReadOnlyList<int> MainOrders {get;init;}=[];
    public bool? NativeMoveLeft {get;init;}
    public bool DirectionConflict {get;init;}
}
public sealed record AuxiliaryLaneCorridor(string Id,AuxiliaryBoundaryKind StartKind,AuxiliaryBoundaryKind EndKind,
    DirectedRoad Origin,ulong? MainEntrance,IReadOnlyList<int> OriginSideOrders,
    IReadOnlyList<AuxiliaryLaneSection> Sections,IReadOnlyList<AuxiliaryLaneTerminal> Terminals,
    double LengthMeters,bool? SideIsLeft);

/// <summary>One immutable recognition result per loaded native map graph. Recognition never accepts a route or position.</summary>
public sealed class AuxiliaryLaneIndex
{
    private sealed record WalkState(DirectedRoad Previous,HashSet<int> Side,HashSet<int> Main,int Count,
        List<AuxiliaryLaneSection> Sections,double Length,HashSet<NativeRoadKey> Visited);
    public NativeLaneGraph Graph {get;}
    public IReadOnlyList<AuxiliaryLaneCorridor> Corridors {get;}
    public IReadOnlyDictionary<NativeRoadKey,IReadOnlyList<AuxiliaryLaneCorridor>> ByRoad {get;}
    public IReadOnlyList<string> Diagnostics {get;}
    /// <summary>Stronger native corridor proof supersedes a geometric lane-reduction classification of this exact merge.</summary>
    public AuxiliaryLaneCorridor? NativeMerge(LaneMergeObservation merge)
    {
        foreach(var corridor in Corridors)
        foreach(var terminal in corridor.Terminals.Where(t=>t.Merges))
        {
            var side=terminal.Edge.LanePaths.Where(p=>p.PrefabUid==merge.PrefabUid&&p.EntryNodeUid==merge.EndingEntryNode
                &&p.EntryLane==merge.EndingEntryLane&&terminal.SideOrders.Contains(p.EntryOrder));
            var main=terminal.Edge.LanePaths.Where(p=>p.EntryNodeUid==merge.MainEntryNode&&p.EntryLane==merge.MainEntryLane
                &&terminal.MainOrders.Contains(p.EntryOrder));
            if(side.Any(s=>main.Any(m=>s.ExitNodeUid==m.ExitNodeUid&&s.ExitLane==m.ExitLane
                &&s.CurveIndices.Count>0&&m.CurveIndices.Count>0&&s.CurveIndices[^1]==m.CurveIndices[^1])))return corridor;
        }
        return null;
    }
    private AuxiliaryLaneIndex(NativeLaneGraph graph,List<AuxiliaryLaneCorridor> corridors,List<string> diagnostics)
    {
        Graph=graph;Corridors=Array.AsReadOnly(corridors.ToArray());Diagnostics=Array.AsReadOnly(diagnostics.Distinct().ToArray());
        ByRoad=corridors.SelectMany(c=>c.Sections.Select(s=>(Key:NativeRoadKey.Of(s.Edge),Corridor:c))
                .Concat(c.Terminals.Select(t=>(Key:NativeRoadKey.Of(t.Edge),Corridor:c))))
            .GroupBy(p=>p.Key).ToFrozenDictionary(g=>g.Key,g=>(IReadOnlyList<AuxiliaryLaneCorridor>)Array.AsReadOnly(g.Select(p=>p.Corridor).DistinctBy(c=>c.Id).ToArray()));
    }

    public static AuxiliaryLaneIndex Build(NativeLaneGraph graph,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var corridors=new List<AuxiliaryLaneCorridor>();var diagnostics=new List<string>();var seeds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var shared in graph.Edges.Where(e=>NativeLaneGraph.Prefab(e) is not null)
            .GroupBy(e=>(Prefab:NativeLaneGraph.Prefab(e),e.EndNodeUid)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entrances=shared.GroupBy(e=>e.StartNodeUid).ToArray();
            if(shared.Any(e=>e.Junction?.IsJunction==true||e.Roundabout is not null))continue;
            if(entrances.Length<2)continue;
            ulong? priority=entrances.SelectMany(g=>g).Select(e=>RoadJoinClassifier.Classify(e).MainEntry).FirstOrDefault(p=>p is not null);
            foreach(var entrance in entrances)
            {
                if(entrance.Key==priority)continue;
                var edge=entrance.First();var side=entrance.SelectMany(e=>e.LanePaths).Select(p=>p.ExitOrder).ToHashSet();
                var others=entrances.Where(g=>g.Key!=entrance.Key&&(priority is null||g.Key==priority))
                    .SelectMany(g=>g).SelectMany(e=>e.LanePaths).Select(p=>p.ExitOrder).ToHashSet();
                if(side.Count==0||others.Count==0||side.Overlaps(others))continue;
                var counts=shared.SelectMany(e=>e.LanePaths).Select(p=>p.ExitLaneCount).Distinct().ToArray();
                if(counts.Length!=1)continue;
                Trace(edge,AuxiliaryBoundaryKind.IndependentEntrance,priority,side,others,counts[0],[],0);
            }
        }
        foreach(var edge in graph.Edges.Where(e=>NativeLaneGraph.Prefab(e) is not null&&e.Junction?.IsJunction!=true&&e.Roundabout is null))
        foreach(var family in edge.LanePaths.GroupBy(p=>p.EntryLane))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(graph.InPrefab(edge).Any(e=>e.StartNodeUid==edge.StartNodeUid&&e.EndNodeUid!=edge.EndNodeUid
                &&(e.LanePaths.Count==0||e.LanePaths.Any(p=>p.EntryLane==family.Key))))continue;
            var retained=family.Where(p=>p.NativeBranchSignal==ForkSignal.None).ToArray();
            foreach(var sidePath in family.Where(p=>p.ExitLaneCount>p.EntryLaneCount&&p.NativeBranchSignal is ForkSignal.Left or ForkSignal.Right))
            {
                var positions=new List<double>();
                foreach(var main in retained)
                {
                    int common=0;while(common<Math.Min(sidePath.CurveIndices.Count,main.CurveIndices.Count)&&sidePath.CurveIndices[common]==main.CurveIndices[common])common++;
                    if(common==0||common>=sidePath.CurveIndices.Count||sidePath.CurveStartPointIndices.Count!=sidePath.CurveIndices.Count)continue;
                    var at=new LanePolyline(edge.Points).Project(sidePath.Points[sidePath.CurveStartPointIndices[common]]);
                    if(at.Error<=15)positions.Add(at.Along);
                }
                if(positions.Count==0)continue;
                double start=positions.Min(),length=RouteGeometry.Length(edge)-start;
                var side=new HashSet<int>{sidePath.ExitOrder};
                var mainOrders=edge.LanePaths.Where(p=>p.ExitOrder!=sidePath.ExitOrder).Select(p=>p.ExitOrder).ToHashSet();
                if(mainOrders.Count==0)continue;
                Trace(edge,AuxiliaryBoundaryKind.NativeSplit,edge.StartNodeUid,side,mainOrders,sidePath.ExitLaneCount,
                    [new(edge,start,RouteGeometry.Length(edge),0,Read(side),Read(mainOrders))],length);
            }
        }
        return new(graph,corridors,diagnostics);

        static IReadOnlyList<int> Read(IEnumerable<int> values)=>Array.AsReadOnly(values.Distinct().Order().ToArray());
        void Trace(DirectedRoad origin,AuxiliaryBoundaryKind startKind,ulong? mainEntrance,HashSet<int> side,HashSet<int> main,
            int count,List<AuxiliaryLaneSection> sections,double length)
        {
            string id=$"auxiliary:{origin.Id}:{string.Join(',',side.Order())}";
            if(!seeds.Add(id))return;
            var initial=Read(side);
            var pending=new Stack<WalkState>();pending.Push(new(origin,side,main,count,sections,length,[]));
            void Missing(string reason)=>diagnostics.Add($"辅助车道结构未完成：{id}；{reason}（后续地图版本补齐后重试）");
            while(pending.TryPop(out var state))
            {
                var previous=state.Previous;side=state.Side;main=state.Main;count=state.Count;
                sections=state.Sections;length=state.Length;var visited=state.Visited;
                while(length<=500)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A shared road node also exposes the opposite-direction prefab. Never turn back into the component just left.
                    var next=graph.From(previous.EndNodeUid).Where(e=>e.EndNodeUid!=previous.StartNodeUid
                        &&(NativeLaneGraph.Prefab(previous) is not {} p||NativeLaneGraph.Prefab(e)!=p)).ToArray();
                    if(next.Length==0){Missing("下游原生道路/预制件尚未加载");break;}
                    var roads=next.Where(e=>NativeLaneGraph.Prefab(e) is null).ToArray();
                    if(roads.Length>0)
                    {
                        if(roads.Length!=1||next.Length!=1){Missing("同方向普通道路连接不唯一");break;}
                        var road=roads[0];if(!visited.Add(NativeRoadKey.Of(road))){Missing("原生连接闭环");break;}
                        double span=RouteGeometry.Length(road);
                        sections.Add(new(road,0,span,length,Read(side),Read(main)));length+=span;previous=road;continue;
                    }
                    if(next.Select(NativeLaneGraph.Prefab).Distinct().Count()!=1){Missing("控制接口对应多个预制件");break;}
                    // These are known, ordinary exclusions, not incomplete map data.
                    if(next.Any(e=>e.Roundabout is not null||e.Junction?.IsJunction==true))break;
                    var sidePaths=next.SelectMany(e=>e.LanePaths).Where(p=>side.Contains(p.EntryOrder)&&p.EntryLaneCount==count).ToArray();
                    var mainPaths=next.SelectMany(e=>e.LanePaths).Where(p=>main.Contains(p.EntryOrder)&&p.EntryLaneCount==count).ToArray();
                    if(sidePaths.Length==0||mainPaths.Length==0){Missing("车道继承缺少原生连接");break;}
                    var terminals=new List<AuxiliaryLaneTerminal>();bool merged=false;
                    foreach(var edge in next)
                    {
                        var sp=sidePaths.Where(p=>p.ExitNodeUid==edge.EndNodeUid).ToArray();var mp=mainPaths.Where(p=>p.ExitNodeUid==edge.EndNodeUid).ToArray();
                        if(sp.Length==0&&mp.Length==0)continue;
                        var positions=new List<double>();var commands=new List<MergeBlinkerDecision>();
                        foreach(var s in sp)foreach(var m in mp.Where(p=>p.ExitLane==s.ExitLane))
                        {
                            int common=0;while(common<Math.Min(s.CurveIndices.Count,m.CurveIndices.Count)&&s.CurveIndices[^(common+1)]==m.CurveIndices[^(common+1)])common++;
                            if(common==0||common>=Math.Min(s.CurveIndices.Count,m.CurveIndices.Count)||s.CurveStartPointIndices.Count!=s.CurveIndices.Count)continue;
                            var at=new LanePolyline(edge.Points).Project(s.Points[s.CurveStartPointIndices[^common]]);
                            if(at.Error<=15){positions.Add(at.Along);commands.Add(MergeBlinkerDirection.Classify(s,m,common));}
                        }
                        if(positions.Count>0)
                        {
                            merged=true;
                            var explicitCommands=commands.Where(c=>c.FirstEnds is not null&&c.MoveLeft is not null).ToArray();
                            var directions=explicitCommands.Select(c=>c.MoveLeft!.Value).Distinct().ToArray();
                            bool? sideLeft=side.Max()<main.Min()?true:side.Min()>main.Max()?false:null;
                            bool conflict=explicitCommands.Any(c=>c.FirstEnds==false)||directions.Length>1
                                ||sideLeft is {} location&&directions.Any(d=>d==location);
                            if(conflict)diagnostics.Add($"辅助车道合流方向冲突：{id}；原生合流转向灯与入口主路群/侧向不一致，保留区间但不按评分生成并入方向");
                            terminals.Add(new(edge,positions.Min(),false,false,true){SideOrders=Read(side),MainOrders=Read(main),
                                NativeMoveLeft=!conflict&&directions.Length==1?directions[0]:null,DirectionConflict=conflict});
                        }
                        else terminals.Add(new(edge,0,sp.Length>0&&mp.Length==0,mp.Length>0&&sp.Length==0){SideOrders=Read(side),MainOrders=Read(main)});
                    }
                    bool separate=terminals.Any(t=>t.SideOnly)&&terminals.Any(t=>!t.SideOnly);
                    if(merged||separate)
                    {
                        double finish=length+(merged?terminals.Where(t=>t.Merges).Select(t=>t.EndMeters).DefaultIfEmpty().Min():0);
                        if(finish<=0||finish>500)break;
                        bool? left=side.Max()<main.Min()?true:side.Min()>main.Max()?false:null;
                        if(mainEntrance is null)diagnostics.Add($"辅助车道区间已确认：{id}；主路身份尚未确定");
                        if(left is null)diagnostics.Add($"辅助车道区间已确认：{id}；辅助群与主路群序位交错，无法得到唯一横向方向");
                        corridors.Add(new(id+":end:"+next[0].Id+":"+string.Join(',',sections.Select(s=>s.Edge.Id)),startKind,merged?AuxiliaryBoundaryKind.NativeMerge:AuxiliaryBoundaryKind.SeparateExit,
                            origin,mainEntrance,initial,Array.AsReadOnly(sections.ToArray()),Array.AsReadOnly(terminals.ToArray()),finish,left));
                    }
                    // Native side paths may both exit here and continue alongside the main group.
                    // Keep each such physical continuation, without introducing a lane-change edge.
                    foreach(var continuation in next)
                    {
                        if(terminals.Any(t=>t.Edge.Id==continuation.Id&&t.Merges))continue;
                        var sp=sidePaths.Where(p=>p.ExitNodeUid==continuation.EndNodeUid).ToArray();
                        var mp=mainPaths.Where(p=>p.ExitNodeUid==continuation.EndNodeUid).ToArray();
                        if(sp.Length==0||mp.Length==0)continue;
                        if(visited.Contains(NativeRoadKey.Of(continuation))){Missing("原生预制件连接闭环");continue;}
                        if(spansMultipleGroups(sp)||spansMultipleGroups(mp)){Missing("同一车道群的输出车道数不一致");continue;}
                        var sideNext=sp.Select(p=>p.ExitOrder).ToHashSet();var mainNext=mp.Select(p=>p.ExitOrder).ToHashSet();
                        if(sideNext.Overlaps(mainNext)){Missing("车道汇合缺少可定位的原生公共曲线");continue;}
                        double nativeLength=RouteGeometry.Length(continuation);
                        var extended=new List<AuxiliaryLaneSection>(sections){new(continuation,0,nativeLength,length,Read(side),Read(main))};
                        var seen=new HashSet<NativeRoadKey>(visited){NativeRoadKey.Of(continuation)};
                        pending.Push(new(continuation,sideNext,mainNext,sp[0].ExitLaneCount,extended,length+nativeLength,seen));
                    }
                    break;
                }
            }
        }
        static bool spansMultipleGroups(PrefabLanePath[] paths)=>paths.Select(p=>p.ExitLaneCount).Distinct().Count()!=1;
    }
}
