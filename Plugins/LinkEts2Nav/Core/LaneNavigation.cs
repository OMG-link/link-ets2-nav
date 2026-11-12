using System.Numerics;
using System.Text.Json.Serialization;
namespace LinkEts2Nav.Core;

public enum LaneBlinker { NoBlinker=0, NoBlinkerForced=1, Right=2, Left=4 }

/// <summary>One explicit PPD chain. Slot numbers are native; orders run left-to-right in travel direction.</summary>
public sealed record PrefabLanePath(ulong PrefabUid,ulong EntryNodeUid,ulong ExitNodeUid,
    int EntryLane,int ExitLane,int EntryOrder,int ExitOrder,int EntryLaneCount,int ExitLaneCount,
    IReadOnlyList<int> CurveIndices,[property:JsonIgnore] IReadOnlyList<Vector3> Points,double LengthMeters)
{
    public IReadOnlyList<int> CurveStartPointIndices { get; init; } = [];
    /// <summary>Native lamp command for each incoming PPD curve, aligned with CurveIndices.</summary>
    public IReadOnlyList<LaneBlinker> CurveBlinkers { get; init; } = [];
    /// <summary>Effective lamp after this lane's native divergence, verified across all native alternatives.</summary>
    public ForkSignal? NativeBranchSignal { get; init; }
}
public sealed record LaneRouteSection(string EdgeId,PrefabLanePath Path);
/// <summary>A road corridor connects consecutive selected prefab paths. A requested lane change is explicit, not a PPD curve.</summary>
public sealed record LaneRouteTransfer(string FromEdgeId,string ToEdgeId,IReadOnlyList<string> RoadIds,
    int FromLane,int ToLane,bool SameLaneVerified)
{
    /// <summary>Virtual planning connection allowed by policy, not a native PPD edge or lane-marking assertion.</summary>
    public bool AllowedLaneChange { get; init; }
}
public sealed record LaneNavigationPlan(IReadOnlyList<LaneRouteSection> Sections,IReadOnlyList<LaneRouteTransfer> Transfers,
    bool Complete,string? Failure)
{
    public LaneRouteTransfer? InitialTransfer { get; init; }
}

/// <summary>Selects concrete lane chains subject to every official entry/exit pair, preferring continuous lanes.</summary>
public sealed class LaneRoutePlanner
{
    private IReadOnlyList<DirectedRoad>? previous;
    private LaneNavigationPlan? cached;
    private (int Edge,int Lane) previousAnchor=(-1,-1);
    public LaneNavigationPlan Build(IReadOnlyList<DirectedRoad> route,Vector3? position=null,double vehicleAlong=0,IReadOnlyList<DirectedRoad>? graph=null)
    {
        int currentEdge=0;double passed=0;
        if(position is not null)
            while(currentEdge<route.Count-1&&passed+new LanePolyline(route[currentEdge].Points).Length<vehicleAlong)
                passed+=new LanePolyline(route[currentEdge++].Points).Length;
        int anchorLane=-1;
        if(position is {} pose&&route.Count>0&&!route[currentEdge].Id.StartsWith("prefab:",StringComparison.Ordinal))
        {
            var next=route.Skip(currentEdge+1).FirstOrDefault(e=>e.Id.StartsWith("prefab:",StringComparison.Ordinal));
            if(next is not null&&next.LanePaths.Count>0)
            {
                var road=new LanePolyline(route[currentEdge].Points);var projection=road.Project(pose);var h=road.Heading(projection.Along);
                double offset=Vector3.Dot(pose-road.At(projection.Along),new(-h.Z,0,h.X));
                var approach=route.TakeWhile(e=>!ReferenceEquals(e,next)).Last();var endLine=new LanePolyline(approach.Points);
                var endHeading=endLine.Heading(endLine.Length);var right=new Vector3(-endHeading.Z,0,endHeading.X);
                anchorLane=next.LanePaths.OrderBy(p=>Math.Abs(Vector3.Dot(p.Points[0]-approach.Points[^1],right)-offset)).First().EntryOrder;
            }
        }
        if(position is {} prefabPose&&route.Count>0&&route[currentEdge].Id.StartsWith("prefab:",StringComparison.Ordinal))
        {
            var edge=route[currentEdge];
            var candidates=(graph??route).Where(e=>e.StartNodeUid==edge.StartNodeUid&&e.Id.StartsWith("prefab:",StringComparison.Ordinal))
                .SelectMany(e=>e.LanePaths).Where(p=>edge.LanePaths.Any(q=>q.PrefabUid==p.PrefabUid)).ToArray();
            if(candidates.Length>0)anchorLane=candidates.OrderBy(p=>new LanePolyline(p.Points).Project(prefabPose).Error).First().EntryOrder;
        }
        if(ReferenceEquals(previous,route)&&previousAnchor==(currentEdge,anchorLane)&&cached is not null)return cached;
        previousAnchor=(currentEdge,anchorLane);
        previous=route;
        if(route.Zip(route.Skip(1)).Any(p=>p.First.EndNodeUid!=p.Second.StartNodeUid))
            return cached=new([],[],false,"路线节点不连续");
        var layers=route.Select((edge,index)=>(edge,index)).Where(p=>p.index>=currentEdge&&p.edge.Id.StartsWith("prefab:",StringComparison.Ordinal)).ToArray();
        if(layers.Length==0)return cached=new([],[],false,"当前路线没有可选择的预制件车道路径");
        if(layers.Any(p=>p.edge.LanePaths.Count==0))return cached=new([],[],false,"预制件车道图缺失，未虚构车道路径");
        var costs=new List<(int Changes,double Length)[]>();var parents=new List<int[]>();
        for(int layer=0;layer<layers.Length;layer++)
        {
            var choices=layers[layer].edge.LanePaths;
            var values=Enumerable.Repeat((Changes:int.MaxValue,Length:double.PositiveInfinity),choices.Count).ToArray();
            var from=Enumerable.Repeat(-1,choices.Count).ToArray();
            for(int j=0;j<choices.Count;j++)
            {
                var choice=choices[j];
                if(choice.EntryNodeUid!=layers[layer].edge.StartNodeUid||choice.ExitNodeUid!=layers[layer].edge.EndNodeUid)continue;
                if(layer==0)
                {
                    int shift=anchorLane<0?0:Math.Abs(anchorLane-choice.EntryOrder);
                    values[j]=(shift,choice.LengthMeters);continue;
                }
                var prior=layers[layer-1].edge.LanePaths;
                bool adjacent=layers[layer].index==layers[layer-1].index+1;
                for(int k=0;k<prior.Count;k++)
                {
                    // Directly attached PPDs must meet physically; no road exists to change lanes on.
                    var transition=Connection(prior[k],choice,adjacent);
                    if(!transition.Possible)continue;
                    bool same=transition.Same;
                    var priorCost=costs[layer-1][k];
                    if(!double.IsFinite(priorCost.Length))continue;
                    var cost=(priorCost.Changes+(same?0:Math.Max(1,Math.Abs(prior[k].ExitOrder-choice.EntryOrder))),priorCost.Length+choice.LengthMeters);
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
            var connection=Connection(a.Path,b.Path,layers[i].index==layers[i-1].index+1);
            bool same=connection.Same;
            transfers.Add(new(a.EdgeId,b.EdgeId,route.Skip(layers[i-1].index+1).Take(layers[i].index-layers[i-1].index-1).Select(e=>e.Id).ToArray(),
                a.Path.ExitOrder,b.Path.EntryOrder,same){AllowedLaneChange=connection.Allowed});
        }
        LaneRouteTransfer? initial=null;
        if(anchorLane>=0&&sections[0].Path.EntryOrder!=anchorLane)
        {
            bool roadApproach=!route[currentEdge].Id.StartsWith("prefab:",StringComparison.Ordinal);
            var path=new LanePolyline(sections[0].Path.Points);var projection=path.Project(position!.Value);
            bool parallel=roadApproach||Vector3.Dot(path.Heading(projection.Along),new LanePolyline(route[currentEdge].Points).Heading(Math.Max(0,vehicleAlong-passed)))>=Math.Cos(Math.PI/180);
            initial=new("vehicle",sections[0].EdgeId,roadApproach?[route[currentEdge].Id]:[],anchorLane,sections[0].Path.EntryOrder,false){AllowedLaneChange=parallel};
        }
        bool complete=transfers.All(t=>t.SameLaneVerified||t.AllowedLaneChange)&&(initial is null||initial.AllowedLaneChange);
        return cached=new(sections,transfers,complete,complete?null:"存在需要变道或车道数变化的道路连接；尚未验证该道路上的变道许可"){InitialTransfer=initial};
    }
    private static (bool Possible,bool Same,bool Allowed) Connection(PrefabLanePath a,PrefabLanePath b,bool adjacent)
    {
        double gap=Vector3.Distance(a.Points[^1],b.Points[0]);
        if(adjacent&&gap<=.5)return(true,true,false);
        bool same=a.ExitLaneCount==b.EntryLaneCount&&a.ExitOrder==b.EntryOrder;
        if(!adjacent)return(true,same,!same&&a.ExitLaneCount==b.EntryLaneCount);
        var before=new LanePolyline(a.Points);var after=new LanePolyline(b.Points);
        var heading=before.Heading(before.Length);var delta=b.Points[0]-a.Points[^1];
        bool parallel=a.ExitLaneCount==b.EntryLaneCount&&Math.Abs(delta.Y)<=.5
            &&Math.Abs(Vector3.Dot(delta,heading))<=1&&gap<=6*Math.Max(1,Math.Abs(a.ExitOrder-b.EntryOrder))
            &&Vector3.Dot(heading,after.Heading(0))>=Math.Cos(Math.PI/180);
        return(parallel,false,parallel);
    }

}
