using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>
/// Build-only dataflow: attach signs, propagate in the native direction to every exit,
/// then materialize directed intervals. All worklists and intermediate states die on return.
/// </summary>
public static class PostedLimitBuilder
{
    private sealed record Event(double At, LimitAction Action, string SignId);
    private sealed record Binding(int Edge, double At, float Error, float Dot);

    public static SpeedLimitData Build(SpeedLimitData data, IReadOnlyList<DirectedRoad> prefabPaths,
        CancellationToken token=default)
    {
        if(data.Error is not null)return data;
        var roads=data.Roads.Values.SelectMany(r=>new[]{r.Geometry,
            r.Geometry with{StartNodeUid=r.Geometry.EndNodeUid,EndNodeUid=r.Geometry.StartNodeUid,Points=r.Geometry.Points.Reverse().ToArray()}});
        var graph=roads.Concat(prefabPaths).Where(e=>e.Points.Count>=2)
            .DistinctBy(LimitEdgeKey.Of).ToArray();
        var starts=graph.Select((e,i)=>(e,i)).GroupBy(p=>p.e.StartNodeUid)
            .ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
        var ends=graph.Select((e,i)=>(e,i)).GroupBy(p=>p.e.EndNodeUid)
            .ToDictionary(g=>g.Key,g=>g.Select(p=>p.i).ToArray());
        var next=graph.Select(e=>(starts.GetValueOrDefault(e.EndNodeUid)??[])
            .Where(i=>graph[i].Id!=e.Id && graph[i].EndNodeUid!=e.StartNodeUid).ToArray()).ToArray();
        var incoming=Enumerable.Range(0,graph.Length).Select(_=>new List<int>()).ToArray();
        for(int i=0;i<graph.Length;i++)foreach(int j in next[i])incoming[j].Add(i);
        var events=Enumerable.Range(0,graph.Length).Select(_=>new List<Event>()).ToArray();
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
                    bindings.Add(new(i,along,p.Value.Error,dot));
                }
            }
            bindings=bindings.GroupBy(b=>b.Edge).Select(g=>g.MinBy(b=>b.Error)!)
                .OrderBy(b=>b.Dot>=.65f?0:1).ThenBy(b=>b.Error).ToList();
            if(bindings.Count==0){warnings.Add("限速标牌未挂载："+sign.Id);continue;}
            var best=bindings[0];
            // Multiple exits can share the same native approach curves: all inherit that sign.
            string? prefab=Prefab(graph[best.Edge]);
            var owners=bindings.Where(b=>b.Error-best.Error<2 && b.Dot>=.65f)
                .Select(b=>Prefab(graph[b.Edge])??graph[b.Edge].Id).Distinct().ToArray();
            bool uncertain=sign.OwnershipAmbiguous||sign.TemplateOverride||best.Dot<.65f||owners.Length>1;
            if(uncertain)warnings.Add("限速标牌归属、朝向或模板未确认："+sign.Id);
            foreach(var binding in bindings.Where(b=>b.Edge==best.Edge || prefab is not null&&Prefab(graph[b.Edge])==prefab&&b.Dot>=.65f&&b.Error-best.Error<2))
                events[binding.Edge].Add(new(binding.At,uncertain?new(Unsupported:true):sign.Action,sign.Id));
        }
        foreach(var list in events)list.Sort((a,b)=>a.At!=b.At?a.At.CompareTo(b.At):StringComparer.Ordinal.Compare(a.SignId,b.SignId));

        // A finite monotone set of rule states handles cycles and avoids order-dependent overwrites.
        // No-state is a boundary default, not an invented posted speed.
        var inputs=Enumerable.Range(0,graph.Length).Select(_=>new HashSet<PostedLimitState>()).ToArray();
        var outputs=Enumerable.Range(0,graph.Length).Select(_=>new HashSet<PostedLimitState>()).ToArray();
        var queue=new Queue<int>();var queued=new bool[graph.Length];
        for(int i=0;i<graph.Length;i++)
        {
            if(incoming[i].Count==0)inputs[i].Add(new());
            if(inputs[i].Count>0||events[i].Count>0){queue.Enqueue(i);queued[i]=true;}
        }
        while(queue.TryDequeue(out int i))
        {
            token.ThrowIfCancellationRequested();queued[i]=false;
            IEnumerable<PostedLimitState> seeds=inputs[i].Count>0?inputs[i]:[new()];
            foreach(var state in seeds)
            {
                var updated=state;
                foreach(var e in events[i])updated=Apply(updated,e.Action);
                if(!outputs[i].Add(updated))continue;
                foreach(int j in next[i])if(inputs[j].Add(updated)&&!queued[j]){queued[j]=true;queue.Enqueue(j);}
            }
        }
        var ranges=new Dictionary<LimitEdgeKey,IReadOnlyList<PostedLimitInterval>>();
        var metadata=new Dictionary<LimitEdgeKey,LimitRoad?>();
        var conflicts=new Dictionary<ulong,int[]>();
        var annotated=data.Roads.ToDictionary(p=>p.Key,p=>p.Value);
        for(int i=0;i<graph.Length;i++)
        {
            token.ThrowIfCancellationRequested();var edge=graph[i];var key=LimitEdgeKey.Of(edge);
            var states=inputs[i].Count>0?new HashSet<PostedLimitState>(inputs[i]):[new PostedLimitState()];
            var limits=states.Where(s=>s.Kph.HasValue).Select(s=>s.Kph!.Value).Distinct().Order().ToArray();
            if(limits.Length>1&&incoming[i].Count>1)conflicts[edge.StartNodeUid]=limits;
            var intervals=new List<PostedLimitInterval>();double from=0;
            double length=edge.Points.Zip(edge.Points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            foreach(var e in events[i])
            {
                double at=Math.Clamp(e.At,0,length);
                if(at>from)intervals.Add(new(from,at,Combine(states)));
                states=states.Select(s=>Apply(s,e.Action)).ToHashSet();from=at;
            }
            intervals.Add(new(from,length,Combine(states)));ranges[key]=intervals.ToArray();
            if(ulong.TryParse(edge.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var uid)&&annotated.TryGetValue(uid,out var road))
            {
                metadata[key]=road;
                annotated[uid]=edge.StartNodeUid==road.Geometry.StartNodeUid
                    ?road with{ForwardSignLimits=intervals.ToArray()}:road with{ReverseSignLimits=intervals.ToArray()};
            }
            else
            {
                // Prefab traffic inherits the adjacent road context, without resetting posted rules.
                var adjacent=(ends.GetValueOrDefault(edge.StartNodeUid)??[]).Concat(starts.GetValueOrDefault(edge.EndNodeUid)??[])
                    .Select(j=>graph[j]).Where(e=>ulong.TryParse(e.Id,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out _))
                    .Select(e=>data.Roads.GetValueOrDefault(ulong.Parse(e.Id,NumberStyles.HexNumber))).OfType<LimitRoad>().ToArray();
                var context=adjacent.FirstOrDefault();
                if(context is not null)
                {
                    var connection=(ends.GetValueOrDefault(edge.StartNodeUid)??[])
                        .Concat(starts.GetValueOrDefault(edge.EndNodeUid)??[]).Select(j=>graph[j])
                        .First(e=>e.Id==context.Geometry.Id);
                    metadata[key]=context with{Geometry=edge,SpeedClass=context.ClassFor(connection),DirectionalClasses=null};
                }
                else metadata[key]=null;
            }
        }
        if(conflicts.Count>0)warnings.Add("限速汇合冲突："+conflicts.Count+" 个节点；"+
            string.Join("；",conflicts.OrderBy(p=>p.Key).Take(8).Select(p=>$"{p.Key:X16}={string.Join('/',p.Value)}")));
        return data with{Roads=annotated,PostedLimits=new PostedLimitIndex(graph,ranges,metadata),
            RegionIndex=new(data.Regions),Diagnostics=warnings.Order(StringComparer.Ordinal).ToArray()};
    }
    private static string? Prefab(DirectedRoad edge)=>edge.Id.StartsWith("prefab:")?edge.Id.Split(':')[1]:null;
    private static PostedLimitState Apply(PostedLimitState state,LimitAction action)
    {
        if(action.Unsupported)return state with{Unknown=true};
        return new(action.SetKph??(action.Reset?null:state.Kph), action.Urban??state.Urban,
            action.Motorway??state.Motorway, action.Reset||action.SetKph.HasValue?false:state.Unknown);
    }
    private static PostedLimitState Combine(HashSet<PostedLimitState> states)
    {
        if(states.Count==1)return states.First();
        var speeds=states.Where(s=>s.Kph.HasValue).Select(s=>s.Kph!.Value).Distinct().ToArray();
        var urban=states.Where(s=>s.Urban.HasValue).Select(s=>s.Urban!.Value).Distinct().ToArray();
        var motorway=states.Where(s=>s.Motorway.HasValue).Select(s=>s.Motorway!.Value).Distinct().ToArray();
        bool unknown=states.Any(s=>s.Unknown)||speeds.Length>1;
        // Context flags are separate from posted numeric limits. Different urban histories
        // do not invalidate an otherwise unanimous explicit speed.
        return new(speeds.Length==1?speeds[0]:null,urban.Length==1?urban[0]:null,
            motorway.Length==1?motorway[0]:null,unknown);
    }
}
