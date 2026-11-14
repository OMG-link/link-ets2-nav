using System.Numerics;
namespace LinkEts2Nav.Core;

public sealed record DedicatedLaneProof(LaneActionInterval Interval,ForkSignal? Signal)
{
    /// <summary>Required lateral movement, proved from native lamps or the relative lane groups; never written back as a native lamp.</summary>
    public bool? MoveLeft {get;init;}
}

/// <summary>Proves where a dedicated exit lane opens on the loaded route.</summary>
public static class DedicatedLaneOpening
{
    public static DedicatedLaneProof? Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        double[] offsets,int fork,IReadOnlyList<int> dedicatedOrders,Action<string>? diagnostic=null)
    {
        var selected=route[fork];
        var native=new NativeLaneGraph(graph);
        var proofs=new List<(double Lead,ForkSignal? Signal,bool? MoveLeft)>();
        var uncertainties=new List<string>();
        foreach(int order in dedicatedOrders.Distinct())
        {
            var chosen=selected.LanePaths.Where(p=>p.EntryOrder==order).ToArray();
            if(chosen.Length==0||chosen.Select(p=>p.EntryLaneCount).Distinct().Count()!=1)return null;
            if(Trace(order,chosen[0].EntryLaneCount) is not {} proof)return null;
            proofs.Add(proof);
        }
        if(proofs.Count==0)return null;
        var first=proofs.MaxBy(p=>p.Lead);
        if(first.Lead>500)return null;
        var directions=proofs.Select(p=>p.MoveLeft??(p.Signal switch
            {ForkSignal.Left=>true,ForkSignal.Right=>false,_=>(bool?)null})).ToArray();
        bool? movement=directions.All(d=>d.HasValue)&&directions.Distinct().Count()==1?directions[0]:null;
        if(movement is null)
            diagnostic?.Invoke("已证明专用辅助车道区间，但无法得到唯一横向方向；"
                +(uncertainties.Count>0?string.Join("；",uncertainties):"目标专用车道分别位于主路两侧"));
        var signals=proofs.Select(p=>p.Signal).Distinct().ToArray();
        return new(new(offsets[fork]-first.Lead,offsets[fork]),signals.Length==1?signals[0]:null){MoveLeft=movement};

        (double Lead,ForkSignal? Signal,bool? MoveLeft)? Trace(int order,int count)
        {
            for(int i=fork-1;i>=0;i--)
            {
                var edge=route[i];
                if(edge.Roundabout is not null)return null;
                if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal))continue;
                var alternatives=native.InPrefab(edge).Where(e=>e.StartNodeUid==edge.StartNodeUid&&e.EndNodeUid!=edge.EndNodeUid).ToArray();
                var paths=edge.LanePaths.Where(p=>p.ExitOrder==order&&p.ExitLaneCount==count).ToArray();
                if(paths.Length==0&&i+1<fork&&!route[i+1].Id.StartsWith("prefab:",StringComparison.Ordinal)
                    &&route[i+1].StartNodeUid==edge.EndNodeUid)
                {
                    // A side entrance can add an independent parallel lane. Once the
                    // shared ordinary-road corridor starts, the lane planner permits
                    // lateral changes within that same directional output group.
                    var joined=native.InPrefab(edge).Where(e=>e.EndNodeUid==edge.EndNodeUid&&e.StartNodeUid!=edge.StartNodeUid)
                        .SelectMany(e=>e.LanePaths).Where(p=>p.ExitLaneCount==count).ToArray();
                    bool ownGroup=edge.LanePaths.Count>0&&edge.LanePaths.All(p=>p.ExitLaneCount==count);
                    bool independentlyAdded=joined.Any(p=>p.ExitOrder==order)
                        &&!joined.Any(p=>edge.LanePaths.Any(q=>q.ExitLane==p.ExitLane));
                    if(ownGroup&&independentlyAdded&&RoadJoinClassifier.Classify(edge).Role==RoadJoinRole.Continuing)
                    {
                        double lead=offsets[fork]-offsets[i+1];
                        if(lead>0)
                        {
                            var mainOrders=edge.LanePaths.Select(p=>p.ExitOrder).Distinct().Order().ToArray();
                            bool? moveLeft=order<mainOrders[0]?true:order>mainOrders[^1]?false:null;
                            if(moveLeft is null)uncertainties.Add($"主路输出车道序位=[{string.Join(",",mainOrders)}]，目标辅助车道序位={order}夹在主路群内（左起0基）");
                            return(lead,null,moveLeft);
                        }
                    }
                }
                if(paths.Length!=1)return null;
                var path=paths[0];
                // An earlier split of this very lane is a separate decision.
                // Unrelated lanes leaving the same prefab do not break its lineage.
                if(alternatives.Any(e=>e.LanePaths.Count==0||e.LanePaths.Any(p=>p.EntryLane==path.EntryLane)))return null;
                if(path.ExitLaneCount>path.EntryLaneCount)
                {
                    var preserved=edge.LanePaths.Where(p=>p.EntryLane==path.EntryLane&&p.ExitLane!=path.ExitLane).ToArray();
                    // The two children share an ancestor; that alone does not make
                    // both lanes new. A native unlit continuation retains the lane,
                    // while its signalled sibling is the newly opened side lane.
                    if(path.NativeBranchSignal==ForkSignal.None&&preserved.Any(p=>p.NativeBranchSignal is ForkSignal.Left or ForkSignal.Right))
                    {order=path.EntryOrder;count=path.EntryLaneCount;continue;}
                    if(path.NativeBranchSignal is not (ForkSignal.Left or ForkSignal.Right))return null;
                    preserved=preserved.Where(p=>p.NativeBranchSignal==ForkSignal.None).ToArray();
                    var positions=new List<double>();
                    foreach(var other in preserved)
                    {
                        int common=0;
                        while(common<Math.Min(path.CurveIndices.Count,other.CurveIndices.Count)
                            &&path.CurveIndices[common]==other.CurveIndices[common])common++;
                        if(common==0||common>=path.CurveIndices.Count||path.CurveStartPointIndices.Count!=path.CurveIndices.Count)continue;
                        var opening=path.Points[path.CurveStartPointIndices[common]];
                        var projection=new LanePolyline(edge.Points).Project(opening);
                        if(projection.Error<=15)positions.Add(offsets[fork]-offsets[i]-projection.Along);
                    }
                    double lead=positions.Where(p=>p>0).DefaultIfEmpty().Max();
                    return lead>0?(lead,path.NativeBranchSignal,null):null;
                }
                // Native lane paths express the mapping across interfaces; lane numbers
                // are local to each interface and need not be equal on the two ends.
                order=path.EntryOrder;count=path.EntryLaneCount;
            }
            return null;
        }
    }
}
