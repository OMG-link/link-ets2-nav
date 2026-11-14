using LinkEts2Nav.Runtime;
using LinkEts2Nav.Core;
using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

/// <summary>Host-owned port/interface membership plus explicitly defined transport links.</summary>
public sealed class FerryJourneyIndex
{
    private readonly Dictionary<ulong,HashSet<string>> portsByUid=[];
    private readonly HashSet<(string From,string To)> connections=[];
    private readonly Dictionary<ulong,FerryPort> activationPorts=[];
    public IReadOnlyList<FerryPort> Ports { get; }
    public FerryJourneyIndex(TruckLib.ScsMap.Map host,IEnumerable<(string From,string To)> links,IReadOnlyDictionary<string,string>? names=null,Func<Road,double?>? roadHalfWidth=null)
    {
        foreach(var (from,to) in links)
        {
            if(string.IsNullOrEmpty(from)||string.IsNullOrEmpty(to)||from==to)continue;
            connections.Add((from,to));connections.Add((to,from));
        }
        var ferries=host.MapItems.Values.OfType<Ferry>().ToDictionary(f=>f.Uid);
        var records=new List<FerryPort>();
        foreach(var ferry in ferries.Values)
        {
            if(ferry.Node is null)continue;
            var prefab=ferry.Prefab is not null?host.MapItems.GetValueOrDefault(ferry.Prefab.Uid) as Prefab:null;
            prefab??=host.MapItems.Values.OfType<Prefab>().FirstOrDefault(p=>p.Ferry?.Uid==ferry.Uid);
            string id=ferry.Port.ToString();
            records.Add(new(id,names?.GetValueOrDefault(id)??id,ferry.Uid,ferry.Node.Uid,ferry.Node.Position,
                prefab?.Uid??0,prefab?.Nodes.Select(n=>n.Uid).ToArray()??[],ferry.TrainTransport)
                {UnloadPosition=ferry.Node.Position+ferry.UnloadOffset, Entrances=BuildEntrances(prefab,ferry,host,roadHalfWidth)});
        }
        Ports=records.AsReadOnly();
        foreach(var port in records.GroupBy(p=>p.Id).Where(g=>g.Count()==1).Select(g=>g.Single()))
        {activationPorts[port.NodeUid]=port;activationPorts[port.ItemUid]=port;}
        void Add(ulong uid,string port)
        {
            if(uid==0||string.IsNullOrEmpty(port))return;
            if(!portsByUid.TryGetValue(uid,out var names))portsByUid[uid]=names=new(StringComparer.Ordinal);
            names.Add(port);
        }
        foreach(var ferry in ferries.Values)
        {
            Add(ferry.Uid,ferry.Port.ToString());
            if(ferry.Node is not null)Add(ferry.Node.Uid,ferry.Port.ToString());
            if(ferry.Prefab is not null&&host.MapItems.GetValueOrDefault(ferry.Prefab.Uid) is Prefab prefab)
                foreach(var node in prefab.Nodes)Add(node.Uid,ferry.Port.ToString());
        }
        // Prefab links can remain unresolved objects; use their UID without
        // relying on the optional reverse Ferry.Prefab reference being populated.
        foreach(var prefab in host.MapItems.Values.OfType<Prefab>())
            if(prefab.Ferry is not null&&ferries.TryGetValue(prefab.Ferry.Uid,out var ferry))
                foreach(var node in prefab.Nodes)Add(node.Uid,ferry.Port.ToString());
    }
    private static IReadOnlyDictionary<ulong,FerryEntrance> BuildEntrances(Prefab? prefab,Ferry ferry,TruckLib.ScsMap.Map host,Func<Road,double?>? width)
    {
        var result=new Dictionary<ulong,FerryEntrance>();
        foreach(var node in prefab?.Nodes??[])
        {
            var roads=host.MapItems.Values.OfType<Road>().Where(r=>r.Node?.Uid==node.Uid||r.ForwardNode?.Uid==node.Uid).ToArray();
            if(roads.Length!=1||width?.Invoke(roads[0]) is not >0)continue;
            var road=roads[0];bool forward=road.ForwardNode.Uid==node.Uid;
            var endpoint=road.InterpolateCurve(forward?1:0);
            var tangent=Vector3.Transform(-Vector3.UnitZ,endpoint.Rotation);if(!forward)tangent=-tangent;
            if(Vector3.Dot(tangent,ferry.Node.Position-node.Position)<0)tangent=-tangent;
            int count=Math.Clamp((int)Math.Ceiling(road.Length/8),16,256);
            var points=Enumerable.Range(0,count+1).Select(i=>road.InterpolateCurve(forward?(float)i/count:1-(float)i/count).Position).ToArray();
            var approach=new DirectedRoad(road.Uid.ToString("X16"),forward?road.Node.Uid:road.ForwardNode.Uid,node.Uid,points);
            result[node.Uid]=new(node.Position,tangent,width(road)!.Value,approach);
        }
        return result;
    }

    public bool Connects(ulong from,ulong to)
        =>from!=to&&portsByUid.TryGetValue(from,out var a)&&portsByUid.TryGetValue(to,out var b)
            &&a.Count==1&&b.Count==1&&connections.Contains((a.Single(),b.Single()));

    public IReadOnlyList<FerryRouteLeg> OnRoute(RouteSnapshot route)
    {
        var nodes=RouteStructureIdentity.Nodes(route);var legs=new List<FerryRouteLeg>();
        var terminal=route.Nodes.MinBy(n=>n.DistanceToEndMeters);
        ulong? goal=!route.MayBeTruncated&&terminal is {DistanceToEndMeters:<=.01f}?terminal.Uid:null;
        for(int i=1;i<nodes.Length;i++)
        {
            if(!activationPorts.TryGetValue(nodes[i-1],out var from)||!activationPorts.TryGetValue(nodes[i],out var to)
                ||from.Train||to.Train||!connections.Contains((from.Id,to.Id)))continue;
            ulong entry=i>=2&&from.Interfaces.Contains(nodes[i-2])?nodes[i-2]:0;
            ulong exit=i+1<nodes.Length&&to.Interfaces.Contains(nodes[i+1])?nodes[i+1]:0;
            if(entry!=0)legs.Add(new(from,to,entry,exit,goal));
        }
        return legs;
    }

    public static FerryJourneyIndex Load(TruckLib.ScsMap.Map host,IFileSystem original,
        IReadOnlyList<string>? mods=null,CancellationToken token=default,Func<Road,double?>? roadHalfWidth=null)
    {
        var fs=SpeedLimitFileSystem.Create(original,mods??[]);
        var links=new List<(string,string)>();
        const string directory="/def/ferry/connection/";
        foreach(var path in fs.GetFiles(directory).Where(p=>p.EndsWith(".sii",StringComparison.Ordinal)
            &&fs.GetParent(p).TrimEnd('/')==directory.TrimEnd('/')).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            foreach(var unit in SiiFile.Open(path,fs).Units.Where(u=>u.Class=="ferry_connection"))
            {
                var name=unit.Name.Split('.');
                if(name.Length==3&&name[0]=="conn")links.Add((name[1],name[2]));
            }
        }
        var names=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in fs.GetFiles("/def/").Where(p=>fs.GetParent(p).TrimEnd('/')=="/def"
            &&Path.GetFileName(p).StartsWith("ferry",StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            foreach(var unit in SiiFile.Open(path,fs).Units.Where(u=>u.Class=="ferry_data"))
                if(unit.Attributes.TryGetValue("ferry_name",out var value))names[unit.Name.Split('.').Last()]=((object)value).ToString()!.Trim('"');
        return new(host,links,names,roadHalfWidth);
    }
}
