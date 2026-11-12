using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using BlinkerType = TruckLib.Models.Ppd.BlinkerType;

namespace LinkEts2Nav.Map;

/// <summary>
/// Reads all native routes, not one shortest chain. State is (curve, lamp mask),
/// bounded by eight states per curve. Masks reset when a later road decision
/// narrows the reachable outlet set, so an earlier independent turn is not inherited.
/// All search state belongs to this one geometry build and is released afterwards.
/// </summary>
public static class PrefabForkTopology
{
    public static IReadOnlyDictionary<ulong,RoadForkInfo> Build(Prefab prefab,PrefabDescriptor descriptor,
        PrefabPlacement placement,Func<int,IReadOnlyDictionary<int,int>> entryOrders)
        =>Analyze(prefab,descriptor,placement,entryOrders,false).Forks;

    /// <summary>Same full native graph analysis, with each output lane as its own decision target.</summary>
    public static IReadOnlyDictionary<(ulong Entry,int Input,ulong Exit,int Output),ForkSignal?> LaneSignals(
        Prefab prefab,PrefabDescriptor descriptor,PrefabPlacement placement,Func<int,IReadOnlyDictionary<int,int>> entryOrders)
        =>Analyze(prefab,descriptor,placement,entryOrders,true).Signals;

    private sealed record Analysis(IReadOnlyDictionary<ulong,RoadForkInfo> Forks,
        IReadOnlyDictionary<(ulong Entry,int Input,ulong Exit,int Output),ForkSignal?> Signals);
    private static Analysis Analyze(Prefab prefab,PrefabDescriptor descriptor,
        PrefabPlacement placement,Func<int,IReadOnlyDictionary<int,int>> entryOrders,bool byLane)
    {
        var curves=descriptor.NavCurves;int count=curves.Count;var nodes=placement.ControlNodes;
        var result=new Dictionary<ulong,RoadForkInfo>();
        var signals=new Dictionary<(ulong,int,ulong,int),ForkSignal?>();
        Analysis Finish()=>new(result,signals);
        if(count==0||nodes.Count>63)return Finish();
        var boundary=new ulong[count];var reach=new ulong[count];var previous=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        var nodeMasks=new ulong[nodes.Count];var targetNodes=byLane?new List<int>():Enumerable.Range(0,nodes.Count).ToList();
        bool valid=true;
        for(int node=0;node<nodes.Count;node++)foreach(int curve in nodes[node].OutputLines.Where(i=>i>=0))
        {
            int target=byLane?targetNodes.Count:node;
            if(curve>=count||boundary[curve]!=0||target>=64){valid=false;continue;}
            if(byLane)targetNodes.Add(node);
            boundary[curve]=1UL<<target;nodeMasks[node]|=boundary[curve];
        }
        for(int i=0;i<count;i++)foreach(int next in curves[i].NextLines)
            if(next<0||next>=count)valid=false;else if(boundary[i]==0)previous[next].Add(i);
        if(!valid)return Finish();
        var queue=new Queue<int>();
        for(int i=0;i<count;i++)if(boundary[i]!=0){reach[i]=boundary[i];queue.Enqueue(i);}
        while(queue.TryDequeue(out int curve))foreach(int parent in previous[curve])
            if((reach[parent]|reach[curve])!=reach[parent]){reach[parent]|=reach[curve];queue.Enqueue(parent);}
        int Lamp(int i)=>curves[i].Blinker switch
        {BlinkerType.NoBlinker or BlinkerType.NoBlinkerForced=>0,BlinkerType.LeftBlinker=>1,BlinkerType.RightBlinker=>2,_=>4};
        for(int from=0;from<nodes.Count;from++)
        {
            var orders=entryOrders(from);ulong excluded=nodeMasks[from],all=0;
            foreach(int start in nodes[from].InputLines.Where(i=>i>=0&&i<count))all|=reach[start]&~excluded;
            if(BitOperations.PopCount(all)<2)continue;
            var outcomes=new Dictionary<(int Exit,int Lane,int Output),HashSet<int>>();
            var decisions=new Dictionary<int,HashSet<NativeForkDecision>>();
            bool inputValid=true;
            for(int lane=0;lane<nodes[from].InputLines.Length;lane++)
            {
                int start=nodes[from].InputLines[lane];if(start<0)continue;
                if(start>=count||!orders.ContainsKey(lane)){inputValid=false;continue;}
                ulong root=reach[start]&~excluded;
                var pending=new Stack<(int Curve,int Lamps)>();var visited=new HashSet<(int,int)>();
                pending.Push((start,BitOperations.PopCount(root)==1?Lamp(start):0));
                while(pending.TryPop(out var state))
                {
                    if(!visited.Add(state))continue;int i=state.Curve;
                    if(boundary[i]!=0)
                    {
                        for(int to=0;to<nodes.Count;to++)if(to!=from&&(boundary[i]&nodeMasks[to])!=0)
                            for(int output=0;output<nodes[to].OutputLines.Length;output++)if(nodes[to].OutputLines[output]==i)
                            {
                                var key=(to,lane,output);
                                if(!outcomes.TryGetValue(key,out var set))outcomes[key]=set=[];
                                set.Add(state.Lamps);
                            }
                        continue;
                    }
                    foreach(int next in curves[i].NextLines)
                    {
                        ulong before=reach[i]&~excluded,after=reach[next]&~excluded;
                        if(after==0)continue;
                        bool narrows=before!=after;
                        int mask=(narrows?0:state.Lamps)|Lamp(next);
                        // Lamps before any road split are common approach instructions.
                        if(after==root&&BitOperations.PopCount(root)>1)mask=0;
                        if(narrows&&BitOperations.PopCount(after)==1)
                        {
                            int to=targetNodes[BitOperations.TrailingZeroCount(after)];
                            if(!decisions.TryGetValue(to,out var points))decisions[to]=points=[];
                            points.Add(new(lane,i,next));
                        }
                        pending.Push((next,mask));
                    }
                }
            }
            var branches=new List<RoadForkBranch>();
            for(int to=0;to<nodes.Count;to++)if((all&nodeMasks[to])!=0)
            {
                var rows=outcomes.Where(p=>p.Key.Exit==to).ToArray();var lamps=rows.SelectMany(p=>p.Value).Distinct().ToArray();
                ForkSignal? Signal(IEnumerable<int> values){var v=values.Distinct().ToArray();return v.Length==1?v[0] switch{0=>ForkSignal.None,1=>ForkSignal.Left,2=>ForkSignal.Right,_=>null}:null;}
                ForkSignal? signal=Signal(lamps);
                foreach(var row in rows)signals[(prefab.Nodes[from].Uid,row.Key.Lane,prefab.Nodes[to].Uid,row.Key.Output)]=Signal(row.Value);
                var inputs=rows.Select(p=>p.Key.Lane).Distinct().Order().ToArray();
                bool inherited=inputs.Any(l=>outcomes.Where(p=>p.Key.Lane==l&&p.Value.Contains(0)).Select(p=>p.Key.Exit).Distinct().SequenceEqual(new[]{to})
                    &&rows.Where(p=>p.Key.Lane==l&&p.Value.Contains(0)).Any(p=>!rows.Any(q=>q.Key.Lane!=l&&q.Key.Output==p.Key.Output&&q.Value.Contains(0))));
                var inputCurve=nodes[from].InputLines.FirstOrDefault(i=>i>=0&&i<count,-1);
                var outputCurve=nodes[to].OutputLines.FirstOrDefault(i=>i>=0&&i<count,-1);
                double? turn=null;
                if(inputCurve>=0&&outputCurve>=0)
                {
                    var incoming=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curves[inputCurve].StartRotation));
                    var outgoing=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curves[outputCurve].EndRotation));
                    if(incoming.X*incoming.X+incoming.Z*incoming.Z>1e-12&&outgoing.X*outgoing.X+outgoing.Z*outgoing.Z>1e-12)
                        turn=Math.Atan2(incoming.X*outgoing.Z-incoming.Z*outgoing.X,
                            incoming.X*outgoing.X+incoming.Z*outgoing.Z);
                }
                branches.Add(new(prefab.Nodes[to].Uid,signal,inputs,inputs.Where(orders.ContainsKey).Select(l=>orders[l]).Order().ToArray(),
                    (decisions.GetValueOrDefault(to)??[]).ToArray(),inherited,signal is null?"目标出口的完整原生路线含不同或冲突的灯光":null)
                    {ExitTurnRadians=turn});
            }
            result[prefab.Nodes[from].Uid]=new(prefab.Nodes[from].Uid,orders.Count,branches,
                inputValid?null:"原生入口车道索引或顺序数据不完整");
        }
        return Finish();
    }
}
