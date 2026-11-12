namespace LinkEts2Nav.Map;

public readonly record struct AngularEdge(int From,int To,double Weight)
{
    // Optional potential-reduced weight. It has the same cycle signs as Weight.
    // Native angular graphs use exact integer winding turns here, so roundoff in
    // telescoping endpoint phases cannot fabricate a tiny negative zero-turn cycle.
    internal double? CycleWeight { get; init; }
}
public readonly record struct AngularPort(int Node,ulong Interface);
public sealed record DistinctPortWalkResult(bool OppositeWalk,bool NegativeCycle,int RelevantNodes,int Edges,int Entrances)
{
    public ulong? EntryInterface { get; init; }
    public ulong? ExitInterface { get; init; }
    public double? Angle { get; init; }
}

/// <summary>Existence of a negative-weight walk between distinct boundary interfaces.
/// O(VE) time, O(V+E) space, independently of the number of interface pairs.</summary>
public static class DistinctPortWalk
{
    private readonly record struct Cost(double Sum,double InitialPhase)
    {
        public double Value(double scale)=>scale*Sum+InitialPhase;
    }
    public static DistinctPortWalkResult Check(int count,IReadOnlyList<AngularEdge> edges,
        IReadOnlyList<AngularPort> entries,IReadOnlyList<AngularPort> exits,CancellationToken token=default,
        IReadOnlyList<double>? endpointPhases=null)
    {
        var outgoing=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        var incoming=Enumerable.Range(0,count).Select(_=>new List<int>()).ToArray();
        foreach(var e in edges){outgoing[e.From].Add(e.To);incoming[e.To].Add(e.From);}
        HashSet<ulong>[] Reach(IReadOnlyList<AngularPort> seeds,List<int>[] links)
        {
            var labels=Enumerable.Range(0,count).Select(_=>new HashSet<ulong>()).ToArray();
            var queue=new Queue<AngularPort>();
            void Add(int node,ulong tag){if(labels[node].Count<2&&labels[node].Add(tag))queue.Enqueue(new(node,tag));}
            foreach(var p in seeds)Add(p.Node,p.Interface);
            while(queue.TryDequeue(out var p)){token.ThrowIfCancellationRequested();foreach(int n in links[p.Node])Add(n,p.Interface);}
            return labels;
        }
        var ins=Reach(entries,outgoing);var outs=Reach(exits,incoming);
        var relevant=Enumerable.Range(0,count).Select(i=>ins[i].Any(a=>outs[i].Any(b=>a!=b))).ToArray();
        var active=edges.Where(e=>relevant[e.From]&&relevant[e.To]).ToArray();
        int n=relevant.Count(v=>v);int k=entries.Select(p=>p.Interface).Distinct().Count();
        DistinctPortWalkResult Result(bool opposite,bool cycle)=>new(opposite,cycle,n,active.Length,k);
        if(n==0)return Result(false,false);
        // Every relevant negative cycle lies on an entry A -> exit B walk, A != B.
        // Repeating it defeats any finite positive prefix/suffix. Ordinary BF avoids
        // conflating changes in a top-two label set with negative-cycle evidence.
        var distances=new double[count];
        for(int round=0;round<n;round++)
        {
            token.ThrowIfCancellationRequested();bool changed=false;
            foreach(var e in active)
                if(distances[e.From]+(e.CycleWeight??e.Weight)<distances[e.To])
                {distances[e.To]=distances[e.From]+(e.CycleWeight??e.Weight);changed=true;}
            if(!changed)break;
            if(round==n-1)return Result(true,true);
        }
        // With no negative cycle, at most n-1 edges suffice. At each vertex, two
        // distinct sources dominate every third source on every common suffix:
        // an exit excludes only its own ONE interface. Synchronous rounds give
        // the ordinary bounded-edge Bellman-Ford induction for these two labels.
        // Native angular paths accumulate integer turns only. Their floating
        // endpoint phase difference is added ONCE, not once per traversed edge.
        double scale=endpointPhases is null?1:2*Math.PI;
        var best=Enumerable.Range(0,count).Select(_=>new Dictionary<ulong,Cost>()).ToArray();
        void Offer(Dictionary<ulong,Cost> labels,ulong source,Cost value)
        {
            if(labels.TryGetValue(source,out var old)&&old.Value(scale)<=value.Value(scale))return;
            labels[source]=value;
            if(labels.Count>2)
            {
                var worst=labels.OrderByDescending(p=>p.Value.Value(scale)).ThenByDescending(p=>p.Key).First();
                labels.Remove(worst.Key);
            }
        }
        foreach(var p in entries.Where(p=>relevant[p.Node]))Offer(best[p.Node],p.Interface,new(0,-(endpointPhases?[p.Node]??0)));
        for(int round=0;round<n-1;round++)
        {
            token.ThrowIfCancellationRequested();
            var next=best.Select(b=>new Dictionary<ulong,Cost>(b)).ToArray();bool changed=false;
            foreach(var e in active)foreach(var p in best[e.From])
            {
                var d=p.Value with{Sum=p.Value.Sum+(endpointPhases is null?e.Weight:e.CycleWeight!.Value)};
                Offer(next[e.To],p.Key,d);
            }
            for(int i=0;i<count;i++)
            {
                if(best[i].Count!=next[i].Count||next[i].Any(p=>!best[i].TryGetValue(p.Key,out var d)||d!=p.Value))changed=true;
            }
            best=next;if(!changed)break;
        }
        foreach(var p in exits)
            foreach(var value in best[p.Node].Where(v=>v.Key!=p.Interface))
            {
                double angle=value.Value.Value(scale)+(endpointPhases?[p.Node]??0);
                if(angle < -CubicWinding.AngleTolerance)
                    return Result(true,false) with {EntryInterface=value.Key,ExitInterface=p.Interface,Angle=angle};
            }
        return Result(false,false);
    }
}
