using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

internal sealed record ProjectedSignEvent(double At,LimitAction Action,string SignId);
internal sealed record SignProjection(List<ProjectedSignEvent>[] Events,IReadOnlyList<string> Diagnostics);

/// <summary>Associates sign events with directed road/native PPD geometry; does not propagate state.</summary>
internal static class SignProjector
{
    private sealed record Binding(int Edge,double At,float Error,float Dot,bool AtEntry,bool AtExit);
    public static SignProjection Build(SpeedLimitData data,DirectedRoad[] graph,CancellationToken token)
    {
        var events=Enumerable.Range(0,graph.Length).Select(_=>new List<ProjectedSignEvent>()).ToArray();
        var warnings=new HashSet<string>(StringComparer.Ordinal);
        foreach(var sign in data.Signs)
        {
            token.ThrowIfCancellationRequested();
            var bindings=new List<Binding>();
            for(int i=0;i<graph.Length;i++)
            {
                var edge=graph[i];
                if(sign.RoadId is not null&&sign.RoadId!=edge.Id)continue;
                // Native PPD chains exclude lateral reference-line connectors at the interfaces.
                var shapes=edge.LanePaths.Count>0?edge.LanePaths.Select(p=>edge with{Points=p.Points}):[edge];
                foreach(var shape in shapes)
                {
                    var p=SpeedLimitGeometry.Nearest(sign.Position,shape);
                    if(p is not {Error:<=18,Vertical:<=3})continue;
                    var heading=new Vector3(sign.Direction.X,0,sign.Direction.Z);
                    float dot=heading.LengthSquared()>.001f?Vector3.Dot(Vector3.Normalize(heading),p.Value.Heading):0;
                    if(dot<-.65f)continue;
                    double shapeLength=shape.Points.Zip(shape.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
                    if(p.Value.Along<.01 && Vector3.Dot(sign.Position-shape.Points[0],p.Value.Heading)<-2)continue;
                    if(shapeLength-p.Value.Along<.01 && Vector3.Dot(sign.Position-shape.Points[^1],p.Value.Heading)>2)continue;
                    var point=sign.Position; // convert native lane distance back to this directed edge's reference distance
                    var along=SpeedLimitGeometry.Nearest(point,edge)!.Value.Along;
                    bindings.Add(new(i,along,p.Value.Error,dot,p.Value.Along<.01,shapeLength-p.Value.Along<.01));
                }
            }
            bindings=bindings.GroupBy(b=>b.Edge).Select(g=>g.MinBy(b=>b.Error)!)
                .OrderBy(b=>b.Dot>=.65f?0:1).ThenBy(b=>b.Error).ToList();
            if(bindings.Count==0){warnings.Add("限速标牌未挂载："+sign.Id);continue;}
            var best=bindings[0];
            // Multiple exits can share the same native approach curves: all inherit that sign.
            string? prefab=Prefab(graph[best.Edge]);
            var nearOwners=bindings.Where(b=>b.Error-best.Error<2 && b.Dot>=.65f).Select(b=>graph[b.Edge]).ToArray();
            bool uncertain=sign.OwnershipAmbiguous||sign.TemplateOverride||best.Dot<.65f||CompetingCorridors(nearOwners);
            if(uncertain)warnings.Add("限速标牌归属、朝向或模板未确认："+sign.Id);
            foreach(var binding in bindings.Where(b=>b.Edge==best.Edge || prefab is not null&&Prefab(graph[b.Edge])==prefab&&b.Dot>=.65f
                && (b.Error-best.Error<2
                    || best.AtExit&&b.AtExit&&graph[b.Edge].EndNodeUid==graph[best.Edge].EndNodeUid
                    || best.AtEntry&&b.AtEntry&&graph[b.Edge].StartNodeUid==graph[best.Edge].StartNodeUid)))
                events[binding.Edge].Add(new(binding.At,uncertain?new(Unsupported:true):sign.Action,sign.Id));
        }
        foreach(var list in events)list.Sort((a,b)=>a.At!=b.At?a.At.CompareTo(b.At):StringComparer.Ordinal.Compare(a.SignId,b.SignId));

        return new(events,warnings.ToArray());
    }
    private static bool CompetingCorridors(IReadOnlyList<DirectedRoad> candidates)
    {
        if(candidates.Count<2)return false;
        var reached=new HashSet<int>{0};var queue=new Queue<int>();queue.Enqueue(0);
        while(queue.TryDequeue(out int i))
        for(int j=0;j<candidates.Count;j++)
        {
            if(reached.Contains(j))continue;
            var a=candidates[i];var b=candidates[j];
            bool sameOwner=a.Id==b.Id || Prefab(a) is {} prefab && prefab==Prefab(b);
            // Two candidates on opposite sides of the same directed interface describe
            // a continuous corridor, not competing ownership of a boundary sign.
            if(sameOwner||a.EndNodeUid==b.StartNodeUid||b.EndNodeUid==a.StartNodeUid)
            {reached.Add(j);queue.Enqueue(j);}
        }
        return reached.Count!=candidates.Count;
    }

    private static string? Prefab(DirectedRoad edge)=>edge.Id.StartsWith("prefab:")?edge.Id.Split(':')[1]:null;
}
