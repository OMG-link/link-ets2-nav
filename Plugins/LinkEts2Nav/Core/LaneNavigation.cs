using System.Numerics;
using System.Text.Json.Serialization;
namespace LinkEts2Nav.Core;

/// <summary>One explicit PPD chain. Slot numbers are native; orders run left-to-right in travel direction.</summary>
public sealed record PrefabLanePath(ulong PrefabUid,ulong EntryNodeUid,ulong ExitNodeUid,
    int EntryLane,int ExitLane,int EntryOrder,int ExitOrder,int EntryLaneCount,int ExitLaneCount,
    IReadOnlyList<int> CurveIndices,[property:JsonIgnore] IReadOnlyList<Vector3> Points,double LengthMeters);
public sealed record LaneRouteSection(string EdgeId,PrefabLanePath Path);
/// <summary>A road corridor connects consecutive selected prefab paths. A requested lane change is explicit, not a PPD curve.</summary>
public sealed record LaneRouteTransfer(string FromEdgeId,string ToEdgeId,IReadOnlyList<string> RoadIds,
    int FromLane,int ToLane,bool SameLaneVerified);
public sealed record LaneNavigationPlan(IReadOnlyList<LaneRouteSection> Sections,IReadOnlyList<LaneRouteTransfer> Transfers,
    bool Complete,string? Failure);

/// <summary>Selects concrete lane chains subject to every official entry/exit pair, preferring continuous lanes.</summary>
public sealed class LaneRoutePlanner
{
    private IReadOnlyList<DirectedRoad>? previous;
    private LaneNavigationPlan? cached;
    public LaneNavigationPlan Build(IReadOnlyList<DirectedRoad> route)
    {
        if(ReferenceEquals(previous,route)&&cached is not null)return cached;
        previous=route;
        if(route.Zip(route.Skip(1)).Any(p=>p.First.EndNodeUid!=p.Second.StartNodeUid))
            return cached=new([],[],false,"路线节点不连续");
        var layers=route.Select((edge,index)=>(edge,index)).Where(p=>p.edge.Id.StartsWith("prefab:",StringComparison.Ordinal)).ToArray();
        if(layers.Length==0)return cached=new([],[],false,"当前路线没有可选择的预制件车道路径");
        if(layers.Any(p=>p.edge.LanePaths.Count==0))return cached=new([],[],false,"预制件车道图缺失，未虚构车道路径");
        var costs=new List<(int Changes,int Shift,double Length)[]>();var parents=new List<int[]>();
        for(int layer=0;layer<layers.Length;layer++)
        {
            var choices=layers[layer].edge.LanePaths;
            var values=Enumerable.Repeat((Changes:int.MaxValue,Shift:int.MaxValue,Length:double.PositiveInfinity),choices.Count).ToArray();
            var from=Enumerable.Repeat(-1,choices.Count).ToArray();
            for(int j=0;j<choices.Count;j++)
            {
                var choice=choices[j];
                if(choice.EntryNodeUid!=layers[layer].edge.StartNodeUid||choice.ExitNodeUid!=layers[layer].edge.EndNodeUid)continue;
                if(layer==0){values[j]=(0,0,choice.LengthMeters);continue;}
                var prior=layers[layer-1].edge.LanePaths;
                bool adjacent=layers[layer].index==layers[layer-1].index+1;
                for(int k=0;k<prior.Count;k++)
                {
                    // Directly attached PPDs must meet physically; no road exists to change lanes on.
                    double gap=Vector3.Distance(prior[k].Points[^1],choice.Points[0]);
                    if(adjacent&&gap>.5)continue;
                    bool same=prior[k].ExitLaneCount==choice.EntryLaneCount&&prior[k].ExitOrder==choice.EntryOrder;
                    var priorCost=costs[layer-1][k];
                    if(!double.IsFinite(priorCost.Length))continue;
                    var cost=(priorCost.Changes+(same?0:1),priorCost.Shift+Math.Abs(prior[k].ExitOrder-choice.EntryOrder),priorCost.Length+choice.LengthMeters);
                    if(cost.CompareTo(values[j])<0){values[j]=cost;from[j]=k;}
                }
            }
            costs.Add(values);parents.Add(from);
        }
        int selected=Enumerable.Range(0,costs[^1].Length).MinBy(i=>costs[^1][i]);
        if(!double.IsFinite(costs[^1][selected].Length))return cached=new([],[],false,"相邻预制件之间没有连续的车道链");
        var sections=new LaneRouteSection[layers.Length];
        for(int i=layers.Length-1;i>=0;i--){sections[i]=new(layers[i].edge.Id,layers[i].edge.LanePaths[selected]);selected=parents[i][selected];}
        var transfers=new List<LaneRouteTransfer>();
        for(int i=1;i<layers.Length;i++)
        {
            var a=sections[i-1];var b=sections[i];
            bool same=a.Path.ExitLaneCount==b.Path.EntryLaneCount&&a.Path.ExitOrder==b.Path.EntryOrder;
            transfers.Add(new(a.EdgeId,b.EdgeId,route.Skip(layers[i-1].index+1).Take(layers[i].index-layers[i-1].index-1).Select(e=>e.Id).ToArray(),
                a.Path.ExitOrder,b.Path.EntryOrder,same));
        }
        bool complete=transfers.All(t=>t.SameLaneVerified);
        return cached=new(sections,transfers,complete,complete?null:"存在需要变道或车道数变化的道路连接；尚未验证该道路上的变道许可");
    }
}
