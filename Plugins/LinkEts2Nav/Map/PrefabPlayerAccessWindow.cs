using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record PrefabPermissionSource(Prefab Prefab,PrefabDescriptor Descriptor);

/// <summary>One build's lane permission proof. All graph and boundary memoization dies after projection.</summary>
public static class PrefabPlayerAccessWindow
{
    private sealed record Port(int Vertex,int Source,int Node,int Slot,bool Input,ulong NodeUid,Vector3 Position,Vector3 Heading);

    public static IReadOnlyDictionary<ulong,PrefabPlayerProjection> Build(IEnumerable<PrefabPermissionSource> sources,
        SpeedLimitDefinitionReader definitions,GameMapData? map=null,Action<string>? diagnostic=null,CancellationToken token=default)
        =>Build(sources,definitions.RoadLanePlayerAccess,definitions.RoadPlayerAccess,definitions.RulePlayerAccess,
            map is null?null:map.ConnectedItems,diagnostic,token);

    internal static IReadOnlyDictionary<ulong,PrefabPlayerProjection> Build(IEnumerable<PrefabPermissionSource> input,
        Func<Road,bool,IReadOnlyList<bool?>?> laneAccess,Func<Road,bool,bool?> directionAccess,Func<string,bool?> ruleAccess,
        Func<ulong,IReadOnlyList<MapItem>>? connected=null,Action<string>? diagnostic=null,CancellationToken token=default)
    {
        var sources=input.ToArray();var starts=new int[sources.Length];int count=0;
        for(int i=0;i<sources.Length;i++){starts[i]=count;count=checked(count+sources[i].Descriptor.NavCurves.Count);}
        var outgoing=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        var incoming=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        var banned=new bool[count];var openIn=new HashSet<int>();var closedIn=new HashSet<int>();
        var openOut=new HashSet<int>();var closedOut=new HashSet<int>();var ports=new List<Port>();
        void Edge(int a,int b){outgoing[a].Add(b);incoming[b].Add(a);}
        void Boundary(Port p,bool? access)
        {if(p.Input)(access==false?closedIn:openIn).Add(p.Vertex);else(access==false?closedOut:openOut).Add(p.Vertex);}
        for(int s=0;s<sources.Length;s++)
        {
            token.ThrowIfCancellationRequested();var source=sources[s];var curves=source.Descriptor.NavCurves;
            PrefabDescriptorInput.AssertCurveIndices(source.Descriptor);
            for(int i=0;i<curves.Count;i++)
            {
                banned[starts[s]+i]=ruleAccess(curves[i].TrafficRule.ToString())==false;
                foreach(int next in curves[i].NextLines)Edge(starts[s]+i,starts[s]+next);
            }
            if(!PrefabPlacement.TryCreate(source.Prefab,source.Descriptor,out var placement))
            {
                // No trustworthy endpoint binding: internal explicit rules still apply.
                for(int i=0;i<curves.Count;i++){openIn.Add(starts[s]+i);openOut.Add(starts[s]+i);}
                diagnostic?.Invoke($"逐车道权限未绑定：Prefab {source.Prefab.Uid:X16} 放置关系不完整。");continue;
            }
            for(int n=0;n<placement.ControlNodes.Count;n++)foreach(bool isInput in new[]{true,false})
            {
                var lines=isInput?placement.ControlNodes[n].InputLines:placement.ControlNodes[n].OutputLines;
                for(int slot=0;slot<lines.Length;slot++)
                {
                    int i=lines[slot];if(i==-1)continue;
                    var c=curves[i];ports.Add(new(starts[s]+i,s,n,slot,isInput,source.Prefab.Nodes[n].Uid,
                        placement.ToWorld(isInput?c.StartPosition:c.EndPosition),
                        placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,isInput?c.StartRotation:c.EndRotation))));
                }
            }
        }
        // Direct prefab attachments use the original native lane slots, not a guessed spatial nearest lane.
        var interfaces=ports.GroupBy(p=>(p.NodeUid,p.Input,p.Slot)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var joined=new HashSet<Port>();
        foreach(var p in ports.Where(p=>!p.Input&&p.NodeUid!=0))
        {
            var matches=(interfaces.GetValueOrDefault((p.NodeUid,true,p.Slot))??[]).Where(q=>q.Source!=p.Source).ToArray();
            if(matches.Length!=1)continue;
            var q=matches[0];
            if((interfaces.GetValueOrDefault((p.NodeUid,false,p.Slot))??[]).Count(e=>e.Source!=q.Source)!=1)continue;
            Edge(p.Vertex,q.Vertex);joined.Add(p);joined.Add(q);
        }
        var owners=new Dictionary<ulong,IReadOnlyList<MapItem>>();
        IReadOnlyList<MapItem> Owners(Port p)
        {
            if(!owners.TryGetValue(p.NodeUid,out var items))
            {
                var node=sources[p.Source].Prefab.Nodes[p.Node];
                owners[p.NodeUid]=items=connected?.Invoke(p.NodeUid)
                    ??new[]{node.ForwardItem,node.BackwardItem}.OfType<MapItem>().DistinctBy(i=>i.Uid).ToArray();
            }
            return items;
        }
        foreach(var group in ports.GroupBy(p=>(p.Source,p.Node,p.Input)))
        {
            token.ThrowIfCancellationRequested();var p=group.First();var lanePorts=group.ToArray();
            var roads=Owners(p).OfType<Road>().Where(r=>r.Node.Uid==p.NodeUid||r.ForwardNode.Uid==p.NodeUid).ToArray();
            if(roads.Length==0){foreach(var port in lanePorts.Where(e=>!joined.Contains(e)))Boundary(port,null);continue;}
            if(roads.Length!=1)
            {
                foreach(var port in lanePorts)Boundary(port,null);
                diagnostic?.Invoke($"逐车道权限未绑定：Prefab {sources[p.Source].Prefab.Uid:X16} 接口 {p.NodeUid:X16} 关联多条道路。");continue;
            }
            var road=roads[0];bool forward=(road.Node.Uid==p.NodeUid)==!p.Input;
            if(directionAccess(road,forward)==false){foreach(var port in lanePorts)Boundary(port,false);continue;}
            var permissions=laneAccess(road,forward);
            var heading=lanePorts.Aggregate(Vector3.Zero,(v,e)=>v+e.Heading);heading.Y=0;
            var right=new Vector3(-heading.Z,0,heading.X);
            var ordered=lanePorts.OrderBy(e=>Vector3.Dot(e.Position,right)).ThenBy(e=>e.Slot).ToArray();
            bool ambiguous=permissions is null||permissions.Count!=ordered.Length||!float.IsFinite(right.LengthSquared())||right.LengthSquared()<1e-8f
                ||ordered.Zip(ordered.Skip(1)).Any(pair=>Math.Abs(Vector3.Dot(pair.Second.Position-pair.First.Position,Vector3.Normalize(right)))<1e-4f)
                ||lanePorts.Any(e=>Vector3.Dot(e.Heading,heading)<=0);
            if(ambiguous)
            {
                foreach(var port in lanePorts)Boundary(port,null);
                if(permissions?.Any(a=>a==false)==true)diagnostic?.Invoke($"逐车道权限未绑定：Road {road.Uid:X16} → Prefab {sources[p.Source].Prefab.Uid:X16} 接口 {p.NodeUid:X16}，方向车道数或横向顺序不唯一。");
                continue;
            }
            for(int i=0;i<ordered.Length;i++)Boundary(ordered[i],permissions![i]);
        }
        bool[] Reach(IEnumerable<int> seeds,List<int>[] edges,bool closed)
        {
            var result=new bool[count];var pending=new Stack<int>(seeds);
            if(closed)foreach(int i in Enumerable.Range(0,count).Where(i=>banned[i]))pending.Push(i);
            while(pending.TryPop(out int i))
            {
                token.ThrowIfCancellationRequested();if(result[i]||!closed&&banned[i])continue;result[i]=true;
                foreach(int next in edges[i])pending.Push(next);
            }
            return result;
        }
        var fromOpen=Reach(openIn,outgoing,false);var fromClosed=Reach(closedIn,outgoing,true);
        var toOpen=Reach(openOut,incoming,false);var toClosed=Reach(closedOut,incoming,true);
        return sources.Select((s,index)=>(s.Prefab.Uid,Projection:PrefabPlayerAccess.Project(s.Descriptor,
            Enumerable.Range(starts[index],s.Descriptor.NavCurves.Count).Select(i=>!banned[i]
                &&!(fromClosed[i]&&!fromOpen[i])&&!(toClosed[i]&&!toOpen[i])).ToArray())))
            .ToDictionary(p=>p.Uid,p=>p.Projection);
    }
}
