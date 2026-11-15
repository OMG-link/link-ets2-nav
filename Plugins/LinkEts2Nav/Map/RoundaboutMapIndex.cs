using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Persistent map annotations. Search memoization exists only inside BuildNewSectors.</summary>
public sealed class RoundaboutMapIndex
{
    private readonly SpecialRoundaboutCatalog special;
    private readonly Dictionary<ulong,SpecialRoundaboutPrefab> resources = [];
    private readonly HashSet<string> diagnostics = [];
    public IReadOnlyList<string> Diagnostics => diagnostics.ToArray();
    public RoundaboutMapIndex(SpecialRoundaboutCatalog? special=null) => this.special=special??SpecialRoundaboutCatalog.Default;
    private readonly HashSet<SectorCoordinate> built = [];
    private readonly Dictionary<SectorCoordinate, Dictionary<ulong,Vector3>> waitingBoundaries = [];
    private readonly HashSet<SectorCoordinate> dirtyAttachments = [];
    public IReadOnlyList<MapQueryIssue> MapIssues {get;private set;}=[];
    public IReadOnlyDictionary<SectorCoordinate, IReadOnlySet<ulong>> UnresolvedBoundaryNodes =>
        waitingBoundaries.ToDictionary(p => p.Key, p => (IReadOnlySet<ulong>)p.Value.Keys.ToHashSet());
    private readonly Dictionary<ulong, (SectorCoordinate Sector, RoundaboutCurve[] Curves, RoundaboutCurveKey[] Entrances,string Access)> prefabs = [];
    private readonly Dictionary<RoundaboutCurveKey, RoundaboutCurve> curves = [];
    private readonly Dictionary<string, RoundaboutStructure> structures = [];
    public IReadOnlyList<RoundaboutStructure> Structures => structures.Values.ToArray();
    public IReadOnlyDictionary<ulong, RoundaboutStructure> RoadAnnotations => structures.Values
        .SelectMany(r => r.PrefabUids.Concat(r.Ring.Select(k => k.ItemUid)).Distinct().Select(uid => (uid, r)))
        .GroupBy(p => p.uid).ToDictionary(g => g.Key, g => g.First().r);
    public int BuiltSectorCount => built.Count;

    public void AddPrefab(Prefab prefab, PrefabDescriptor descriptor, string? resource=null,string access="native")
    {
        if(prefabs.TryGetValue(prefab.Uid,out var previous)&&previous.Access==access)return;
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return;
        if(previous.Curves is not null)
        {
            // Additional permission evidence creates a new structural version.
            // Unchanged sectors still build once; invalidate only affected owners.
            var affected=structures.Values.Where(r=>r.PrefabUids.Contains(prefab.Uid)).ToArray();
            foreach(var structure in affected)
            {
                structures.Remove(structure.Id);
                foreach(ulong member in structure.PrefabUids)
                    if(prefabs.TryGetValue(member,out var owned))built.Remove(owned.Sector);
            }
            built.Remove(previous.Sector);
            foreach(var curve in previous.Curves)curves.Remove(curve.Key);
            string uid=prefab.Uid.ToString("X16");
            diagnostics.RemoveWhere(d=>d.Contains(uid,StringComparison.OrdinalIgnoreCase));
        }
        if(resource is not null)resources[prefab.Uid]=new(resource,descriptor.NavCurves.Count,descriptor.Nodes.Count);
        var entry = new Dictionary<int, ulong>(); var exit = new Dictionary<int, ulong>();
        var entryLanes = new Dictionary<int,int>(); var exitLanes=new Dictionary<int,int>();
        for (int n = 0; n < placement.ControlNodes.Count; n++)
        {
            foreach (int i in placement.ControlNodes[n].InputLines.Where(i => i >= 0)) entry[i] = prefab.Nodes[n].Uid;
            foreach (int i in placement.ControlNodes[n].OutputLines.Where(i => i >= 0)) exit[i] = prefab.Nodes[n].Uid;
            for(int lane=0;lane<placement.ControlNodes[n].InputLines.Length;lane++)entryLanes[placement.ControlNodes[n].InputLines[lane]]=lane;
            for(int lane=0;lane<placement.ControlNodes[n].OutputLines.Length;lane++)exitLanes[placement.ControlNodes[n].OutputLines[lane]]=lane;
        }
        var result = new List<RoundaboutCurve>();
        for (int i = 0; i < descriptor.NavCurves.Count; i++)
        {
            var curve = descriptor.NavCurves[i];
            if (!float.IsFinite(curve.Length) || curve.Length <= 0) continue;
            var a = Vector3.Transform(-Vector3.UnitZ, curve.StartRotation) * curve.Length;
            var b = Vector3.Transform(-Vector3.UnitZ, curve.EndRotation) * curve.Length;
            int samples = Math.Max(4, (int)Math.Ceiling(curve.Length / 1.5));
            if (samples > 4096) continue;
            var points = Enumerable.Range(0, samples + 1).Select(j =>
            {
                float t = (float)j / samples, t2 = t * t, t3 = t2 * t;
                return placement.ToWorld((2 * t3 - 3 * t2 + 1) * curve.StartPosition + (t3 - 2 * t2 + t) * a
                    + (-2 * t3 + 3 * t2) * curve.EndPosition + (t3 - t2) * b);
            }).ToArray();
            if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))) continue;
            result.Add(new(new(prefab.Uid, i), prefab.Uid, prefab.LeftHandTraffic, points,
                curve.NextLines.Where(j=>j>=0).Select(j => new RoundaboutCurveKey(prefab.Uid, j)).ToArray(), entry.GetValueOrDefault(i), exit.GetValueOrDefault(i))
            {NativeCurve=Native(placement.ToWorld(curve.StartPosition),placement.ToWorld(curve.EndPosition),
                placement.ToWorldDirection(a),placement.ToWorldDirection(b)),EntryLane=entryLanes.GetValueOrDefault(i,-1),ExitLane=exitLanes.GetValueOrDefault(i,-1)});
        }
        var sector = TruckLib.ScsMap.Map.GetSectorOfCoordinate(prefab.Nodes[0].Position);
        prefabs[prefab.Uid] = (sector, result.ToArray(), entry.Keys.Select(i => new RoundaboutCurveKey(prefab.Uid, i)).ToArray(),access);
        foreach (var curve in result) curves[curve.Key] = curve;
        AttachmentsAdded(result.Select(c => c.EntryNode));
    }

    private static CubicPlaneCurve Native(Vector3 p,Vector3 q,Vector3 u,Vector3 v)
        => CubicPlaneCurve.Hermite(new(p.X,-(double)p.Z),new(q.X,-(double)q.Z),new(u.X,-(double)u.Z),new(v.X,-(double)v.Z));

    public void AddRoad(Road road,SpeedLimitDefinitionReader? definitions=null)
    {
        if (road.Node is null || road.ForwardNode is null || curves.ContainsKey(new(road.Uid, -1))) return;
        int samples = Math.Clamp((int)Math.Ceiling(road.Length / 3), 8, 1024);
        var interpolated=Enumerable.Range(0,samples+1).Select(i=>road.InterpolateCurve((float)i/samples)).ToArray();
        var points=interpolated.Select(p=>p.Position).ToArray();
        var normals=interpolated.Select(p=>Vector3.Normalize(Vector3.Transform(Vector3.UnitX,p.Rotation))).ToArray();
        var native=Native(road.Node.Position,road.ForwardNode.Position,
            Vector3.Transform(new Vector3(0,0,-road.Length),road.Node.Rotation),
            Vector3.Transform(new Vector3(0,0,-road.Length),road.ForwardNode.Rotation));
        var traffic=definitions?.RoadTrafficDirections(road);
        // Road states describe road connectivity, not a claim about exact lane trajectories.
        curves[new(road.Uid, -1)] = new(new(road.Uid, -1), null, road.LeftHandTraffic, points, [], road.Node.Uid, road.ForwardNode.Uid)
            {NativeCurve=native,ProvenTraffic=traffic?.Forward==true,TrafficKnown=traffic?.Forward.HasValue==true,RoadNormals=normals};
        curves[new(road.Uid, -2)] = new(new(road.Uid, -2), null, road.LeftHandTraffic, points.Reverse().ToArray(), [], road.ForwardNode.Uid, road.Node.Uid)
            {NativeCurve=native.Reverse(),ProvenTraffic=traffic?.Reverse==true,TrafficKnown=traffic?.Reverse.HasValue==true,
                RoadNormals=normals.Reverse().Select(n=>-n).ToArray()};
        AttachmentsAdded([road.Node.Uid, road.ForwardNode.Uid]);
    }

    // Invalidate only searches that actually reached an attachment now supplied
    // by new geometry. Loading an unrelated sector does not repeat old searches.
    private void AttachmentsAdded(IEnumerable<ulong> nodes)
    {
        var added = nodes.Where(n => n != 0).ToHashSet();
        foreach (var pending in waitingBoundaries.ToArray())
            if (pending.Value.Keys.Any(added.Contains))
            {
                built.Remove(pending.Key);
                dirtyAttachments.Add(pending.Key);
                foreach(var node in added)pending.Value.Remove(node);
                if (pending.Value.Count == 0) waitingBoundaries.Remove(pending.Key);
            }
    }

    public void BuildNewSectors(IEnumerable<SectorCoordinate> sectors, CancellationToken token = default,
        Func<ulong,MapQueryIssue>? missingConnection=null)
    {
        // A completed attachment can dirty an older search cell outside the new
        // active window. Its native curves are already retained by this index.
        var fresh = sectors.Concat(dirtyAttachments)
            .Where(s => !built.Contains(s)).Distinct().ToArray();
        var inputs = curves.Values.Where(c => c.EntryNode != 0).GroupBy(c => c.EntryNode).ToDictionary(g => g.Key, g => g.ToArray());
        var graph = curves.Values.Select(c =>
        {
            if (c.ExitNode == 0) return c;
            var next = (inputs.GetValueOrDefault(c.ExitNode) ?? []).Where(n => n.Key.ItemUid != c.Key.ItemUid)
                .Select(n => n.Key).ToArray();
            return c with { Next = c.Next.Concat(next).Distinct().ToArray() };
        }).ToArray();
        var linked=graph.ToDictionary(c=>c.Key);
        var classified=structures.Values.Where(r=>r.SpecialDefinitionId is not null).SelectMany(r=>r.PrefabUids).ToHashSet();
        foreach(var declared in special.Build(linked,resources,classified,message=>diagnostics.Add(message),token))
        {
            if(declared.DirectionCheck is { } check)diagnostics.Add($"Declared {declared.SpecialDefinitionId}: {check}");
            if(declared.LaneGeometry is { } geometry)diagnostics.Add($"Declared {declared.SpecialDefinitionId}: {geometry}");
            foreach(var automatic in structures.Values.Where(r=>r.SpecialDefinitionId is null
                &&r.PrefabUids.Intersect(declared.PrefabUids).Any()).Select(r=>r.Id).ToArray())structures.Remove(automatic);
            structures[declared.Id]=declared;
            classified.UnionWith(declared.PrefabUids);
        }
        foreach (var sector in fresh)
        {
            token.ThrowIfCancellationRequested();
            // All dictionaries used to memoize this sector's search die with this builder.
            var builder = new RoundaboutBuilder(graph);
            var additions = new List<RoundaboutStructure>();
            var missing = new Dictionary<ulong,Vector3>();
            foreach (var pair in prefabs.Where(p => p.Value.Sector == sector).OrderBy(p => p.Key))
            {
                if (structures.Values.Concat(additions).Any(r => r.PrefabUids.Contains(pair.Key))) continue;
                var found = builder.Find(pair.Key, pair.Value.Entrances, token);
                if (found is not null) additions.Add(found);
                else if(pair.Value.Curves.FirstOrDefault(c=>c.Points.Count>0) is {} seed)
                    foreach(var node in builder.UnresolvedBoundaryNodes)missing.TryAdd(node,seed.Points[0]);
            }
            foreach(var check in builder.DirectionChecks)diagnostics.Add(check.ToString());
            foreach(var geometry in builder.GeometryChecks)diagnostics.Add(geometry.ToString());
            token.ThrowIfCancellationRequested();
            foreach (var found in additions) structures.TryAdd(found.Id, found);
            if (missing.Count == 0) waitingBoundaries.Remove(sector);
            else waitingBoundaries[sector] = missing;
            built.Add(sector);
            dirtyAttachments.Remove(sector);
        }
        MapIssues=waitingBoundaries.Values.SelectMany(nodes=>nodes).Distinct()
            .Select(p=>(missingConnection?.Invoke(p.Key)
                ??new MapQueryFailure(MapQueryFailureKind.UnresolvedReference,$"环岛搜索接口{p.Key:X16}未解析外部附件，缺少其所属区块证据"))
                with{QueryAnchor=p.Value})
            .ToArray();
    }
}
