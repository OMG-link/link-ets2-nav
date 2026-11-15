using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record PrefabPlayerProjection(PrefabDescriptor Descriptor,string CacheKey,int ExcludedCurves);

/// <summary>A player-drivable view of native curves. Unknown permissions retain native connectivity.</summary>
public static class PrefabPlayerAccess
{
    public static PrefabPlayerProjection Build(Prefab prefab,PrefabDescriptor source,SpeedLimitDefinitionReader definitions,GameMapData? map=null)
    {
        int count=source.Nodes.Count;
        if(prefab.Nodes.Count!=count||prefab.Origin>=count)return new(source,"native",0);
        var inputs=new bool?[count];var outputs=new bool?[count];
        for(int n=0;n<count;n++)
        {
            var node=prefab.Nodes[(n-prefab.Origin+count)%count];
            // Host nodes may still reference UnresolvedItem after the manager has
            // supplemented the filtered road. Resolve its UID through the shared map.
            var roads=(map?.ConnectedItems(node.Uid)??new[]{node.ForwardItem,node.BackwardItem}.OfType<MapItem>().ToArray())
                .OfType<Road>().DistinctBy(r=>r.Uid).ToArray();
            bool? Access(bool leaving)
            {
                var states=roads.Where(r=>r.Node.Uid==node.Uid||r.ForwardNode.Uid==node.Uid)
                    .Select(r=>definitions.RoadPlayerAccess(r,(r.Node.Uid==node.Uid)==leaving)).Distinct().ToArray();
                return states.Length==1?states[0]:null;
            }
            inputs[n]=Access(false);outputs[n]=Access(true);
        }
        return Build(source,inputs,outputs,definitions.RulePlayerAccess);
    }

    public static PrefabPlayerProjection Build(PrefabDescriptor source,IReadOnlyList<bool?> inputs,
        IReadOnlyList<bool?> outputs,Func<string,bool?>? ruleAccess=null)
    {
        int count=source.NavCurves.Count;
        if(count>8192||inputs.Count!=source.Nodes.Count||outputs.Count!=source.Nodes.Count)return new(source,"native",0);
        bool Valid(int i)=>i>=0&&i<count;
        var explicitBan=source.NavCurves.Select(c=>ruleAccess?.Invoke(c.TrafficRule.ToString())==false).ToArray();
        var reverse=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        for(int i=0;i<count;i++)foreach(int next in source.NavCurves[i].NextLines.Where(Valid))reverse[next].Add(i);
        bool[] Reach(bool input,bool closed)
        {
            var reached=new bool[count];var pending=new Stack<int>();
            for(int n=0;n<source.Nodes.Count;n++)
                if(((input?inputs[n]:outputs[n])==false)==closed)
                    foreach(int i in (input?source.Nodes[n].InputLines:source.Nodes[n].OutputLines).Where(Valid))pending.Push(i);
            if(closed)for(int i=0;i<count;i++)if(explicitBan[i])pending.Push(i);
            while(pending.TryPop(out int i))
            {
                if(reached[i]||!closed&&explicitBan[i])continue;reached[i]=true;
                foreach(int next in input?source.NavCurves[i].NextLines.Where(Valid):reverse[i])pending.Push(next);
            }
            return reached;
        }
        var fromOpen=Reach(true,false);var fromClosed=Reach(true,true);
        var toOpen=Reach(false,false);var toClosed=Reach(false,true);
        var allowed=Enumerable.Range(0,count).Select(i=>!explicitBan[i]
            &&!(fromClosed[i]&&!fromOpen[i])&&!(toClosed[i]&&!toOpen[i])).ToArray();
        return Project(source,allowed);
    }

    internal static PrefabPlayerProjection Project(PrefabDescriptor source,IReadOnlyList<bool> allowed)
    {
        if(allowed.Count!=source.NavCurves.Count)throw new ArgumentException("Permission count must match native curves.",nameof(allowed));
        int count=source.NavCurves.Count;
        bool Valid(int i)=>i>=0&&i<count;
        int excluded=allowed.Count(a=>!a);
        if(excluded==0)return new(source,"native",0);
        bool Keep(int i)=>Valid(i)&&allowed[i];
        var projected=new PrefabDescriptor
        {
            Nodes=source.Nodes.Select(n=>new ControlNode{Position=n.Position,Direction=n.Direction,TerrainPoints=n.TerrainPoints,
                InputLines=n.InputLines.Select(i=>Keep(i)?i:-1).ToArray(),OutputLines=n.OutputLines.Select(i=>Keep(i)?i:-1).ToArray()}).ToList(),
            NavCurves=source.NavCurves.Select((c,i)=>Copy(c,allowed[i],Keep)).ToList(),
            Signs=source.Signs,Semaphores=source.Semaphores,SpawnPoints=source.SpawnPoints,MapPoints=source.MapPoints,
            TriggerPoints=source.TriggerPoints,Intersections=source.Intersections,NavNodes=source.NavNodes
        };
        return new(projected,string.Concat(allowed.Select(a=>a?'1':'0')),excluded);
    }

    private static NavCurve Copy(NavCurve c,bool allowed,Func<int,bool> keep)
    {
        var result=new NavCurve{Name=c.Name,LeadsToNodes=c.LeadsToNodes,StartPosition=c.StartPosition,EndPosition=c.EndPosition,
            StartRotation=c.StartRotation,EndRotation=c.EndRotation,Length=allowed?c.Length:0,SemaphoreId=c.SemaphoreId,
            TrafficRule=c.TrafficRule,NavNodeIndex=c.NavNodeIndex,Blinker=c.Blinker,AllowedVehicles=c.AllowedVehicles,
            LowProbability=c.LowProbability,LimitDisplacement=c.LimitDisplacement,AdditivePriority=c.AdditivePriority,PriorityModifier=c.PriorityModifier};
        if(allowed)
        {
            foreach(int i in c.NextLines.Where(keep))result.NextLines.Add(i);
            foreach(int i in c.PreviousLines.Where(keep))result.PreviousLines.Add(i);
        }
        return result;
    }
}
