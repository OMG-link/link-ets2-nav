using System.Diagnostics;

namespace LinkEts2Nav.Map;

public sealed record RoundaboutDirectionResult(string Candidate,bool OppositeWalk,bool NegativeCycle,
    bool Complete,string? Uncertainty,PlanePoint? Center,int V,int E,int K,
    double CenterMilliseconds,double WeightMilliseconds,double SearchMilliseconds,double TotalMilliseconds)
{
    public ulong? EntryInterface { get; init; }
    public ulong? ExitInterface { get; init; }
    public double? Angle { get; init; }
    public bool NativeClosedReference { get; init; }
}
/// <summary>Optional one-shot review export. The builder retains no diagnostic graph cache.</summary>
public sealed record RoundaboutDirectionAudit(PlanePoint Center,double Rotation,
    IReadOnlyList<CubicPlaneCurve> Reference,IReadOnlyList<RoundaboutCurve> Curves,
    IReadOnlyList<AngularEdge> Edges,IReadOnlyList<AngularPort> Entries,IReadOnlyList<AngularPort> Exits);

/// <summary>Session-local direction veto over ALL real routes of the reference circuit's objects.</summary>
public static class RoundaboutDirection
{
    public static RoundaboutDirectionResult Check(IReadOnlyDictionary<RoundaboutCurveKey,RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring,CancellationToken token=default,Action<RoundaboutDirectionAudit>? audit=null)
    {
        var total=Stopwatch.StartNew();var phase=Stopwatch.StartNew();
        string id=string.Join("/",ring.Select(k=>k.ItemUid).Distinct().Order().Select(k=>$"{k:X16}"));
        var items=ring.Select(k=>k.ItemUid).ToHashSet();
        var local=graph.Values.Where(c=>items.Contains(c.Key.ItemUid)).OrderBy(c=>c.Key.ItemUid).ThenBy(c=>c.Key.CurveIndex).ToArray();
        PlanePoint? center=null;double centerMs=0,weightMs=0,searchMs=0;bool complete=true;string? uncertainty=null;
        int vertices=local.Length*2,edgeCount=0,k=0;
        bool closed=ring.Select((key,i)=>graph[key].Next.Contains(ring[(i+1)%ring.Count])).All(v=>v);
        RoundaboutDirectionResult Result(bool opposite=false,bool cycle=false)=>new(id,opposite,cycle,complete,uncertainty,center,vertices,edgeCount,k,centerMs,weightMs,searchMs,total.Elapsed.TotalMilliseconds)
            {NativeClosedReference=closed};
        void Unknown(string message){complete=false;uncertainty??=message;}
        if(ring.Any(key=>graph[key].NativeCurve is null))
        {Unknown("Native reference coefficients unavailable (synthetic/legacy geometry).");return Result();}
        if(!closed)Unknown("Declared reference is not a native closed walk; its explicit endpoint attachments define the integration circuit.");
        try
        {
            CubicPlaneCurve Connector(PlanePoint p,PlanePoint q)=>new(new(0,0),new(0,0),q-p,p);
            var reference=new List<CubicPlaneCurve>();
            for(int i=0;i<ring.Count;i++)
            {
                var curve=graph[ring[i]].NativeCurve!;reference.Add(curve);
                reference.Add(Connector(curve.At(1),graph[ring[(i+1)%ring.Count]].NativeCurve!.D));
            }
            center=CubicWinding.Centroid(reference);centerMs=phase.Elapsed.TotalMilliseconds;phase.Restart();
            var native=local.Select(c=>c.NativeCurve?.Shift(center.Value)).ToArray();
            if(local.Select(c=>c.LeftHandTraffic).Distinct().Count()!=1)
            {Unknown("Member objects disagree on actual traffic side.");return Result();}
            // Each connector is a topological interface attachment, NOT a claim
            // that drivers follow a segment to a road control-point centre.
            // Its endpoints must share a centre-free open half-plane. Within that
            // local attachment patch winding equals the endpoint angular potential.
            bool SafeAttachment(PlanePoint p,PlanePoint q)
                => p.X*q.X+p.Y*q.Y>0;
            var index=local.Select((c,i)=>(c.Key,i)).ToDictionary();
            var internalNodes=local.SelectMany(c=>new[]{(Node:c.EntryNode,c.Key.ItemUid),(Node:c.ExitNode,c.Key.ItemUid)})
                .Where(p=>p.Node!=0).GroupBy(p=>p.Node).Where(g=>g.Select(p=>p.ItemUid).Distinct().Skip(1).Any()).Select(g=>g.Key).ToHashSet();
            bool Boundary(ulong node)=>node!=0&&!internalNodes.Contains(node);
            var entries=new List<AngularPort>();var exits=new List<AngularPort>();
            var geometricEdges=new List<(int From,int To,CubicPlaneCurve Curve)>();
            for(int i=0;i<local.Length;i++)
            {
                token.ThrowIfCancellationRequested();var c=local[i];
                if(!c.TrafficKnown||native[i] is null){Unknown("Unresolved native geometry or directed road traffic.");continue;}
                if(!c.ProvenTraffic)continue;
                if(c.Next.Any(next=>items.Contains(next.ItemUid)&&!index.ContainsKey(next)))
                    Unknown("A native successor inside the component has missing geometry.");
                geometricEdges.Add((2*i,2*i+1,native[i]!));
                if(Boundary(c.EntryNode))entries.Add(new(2*i,c.EntryNode));
                if(Boundary(c.ExitNode))exits.Add(new(2*i+1,c.ExitNode));
                foreach(var next in c.Next)
                {
                    if(!index.TryGetValue(next,out int j)||!local[j].ProvenTraffic||native[j] is null)continue;
                    var target=local[j];
                    bool same=c.Key.ItemUid==target.Key.ItemUid;
                    if(!same)
                    {
                        if(c.ExitNode==0||c.ExitNode!=target.EntryNode)continue;
                        // Direct prefab interfaces retain the actual native lane slot.
                        // A road object is a same-direction traffic corridor and permits
                        // natural lane changes; its opposite direction is a separate edge.
                        if(c.PrefabUid is not null&&target.PrefabUid is not null
                            &&(c.ExitLane<0||c.ExitLane!=target.EntryLane))continue;
                    }
                    var p=native[i]!.At(1);var q=native[j]!.D;
                    if(!SafeAttachment(p,q)){Unknown("Interface attachment not confined to a centre-free local half-plane.");continue;}
                    geometricEdges.Add((2*i+1,2*j,Connector(p,q)));
                }
            }
            // The same separation check applies to the reference's native endpoint
            // attachments before its centroid can be trusted for this component.
            for(int i=0;i<ring.Count;i++)
                if(!SafeAttachment(graph[ring[i]].NativeCurve!.At(1)-center.Value,
                    graph[ring[(i+1)%ring.Count]].NativeCurve!.D-center.Value))
                {Unknown("Reference interface crosses the winding centre's half-plane.");return Result();}
            double rotation=CubicWinding.Rotation(geometricEdges.Select(e=>e.Curve).ToArray());
            var edges=new List<AngularEdge>();double sign=local[0].LeftHandTraffic?-1:1;
            double Phase(int node)
            {
                var c=native[node/2]!.Rotate(rotation);var p=node%2==0?c.D:c.At(1);
                return sign*Math.Atan2(p.Y,p.X);
            }
            foreach(var e in geometricEdges)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    double angle=sign*CubicWinding.Angle(e.Curve.Rotate(rotation));
                    double turns=(angle-Phase(e.To)+Phase(e.From))/(2*Math.PI);
                    double integer=Math.Round(turns);
                    if(Math.Abs(turns-integer)>CubicWinding.AngleTolerance)
                        throw new InvalidDataException("Angular edge disagrees with native endpoint phases.");
                    edges.Add(new(e.From,e.To,angle){CycleWeight=integer});
                }
                catch(InvalidDataException ex){Unknown(ex.Message);}
            }
            edgeCount=edges.Count;k=entries.Select(p=>p.Interface).Distinct().Count();
            audit?.Invoke(new(center.Value,rotation,reference,local,edges,entries,exits));
            weightMs=phase.Elapsed.TotalMilliseconds;phase.Restart();
            var result=DistinctPortWalk.Check(vertices,edges,entries,exits,token,
                Enumerable.Range(0,vertices).Select(i=>native[i/2] is null?0:Phase(i)).ToArray());
            searchMs=phase.Elapsed.TotalMilliseconds;
            return Result(result.OppositeWalk,result.NegativeCycle) with
            {EntryInterface=result.EntryInterface,ExitInterface=result.ExitInterface,Angle=result.Angle};
        }
        catch(InvalidDataException ex){Unknown(ex.Message);return Result();}
    }
}
