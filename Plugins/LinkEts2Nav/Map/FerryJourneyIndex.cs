using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

/// <summary>Host-owned port/interface membership plus explicitly defined transport links.</summary>
public sealed class FerryJourneyIndex
{
    private readonly Dictionary<ulong,HashSet<string>> portsByUid=[];
    private readonly HashSet<(string From,string To)> connections=[];
    public FerryJourneyIndex(TruckLib.ScsMap.Map host,IEnumerable<(string From,string To)> links)
    {
        foreach(var (from,to) in links)
        {
            if(string.IsNullOrEmpty(from)||string.IsNullOrEmpty(to)||from==to)continue;
            connections.Add((from,to));connections.Add((to,from));
        }
        var ferries=host.MapItems.Values.OfType<Ferry>().ToDictionary(f=>f.Uid);
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
    public bool Connects(ulong from,ulong to)
        =>from!=to&&portsByUid.TryGetValue(from,out var a)&&portsByUid.TryGetValue(to,out var b)
            &&a.Count==1&&b.Count==1&&connections.Contains((a.Single(),b.Single()));

    public static FerryJourneyIndex Load(TruckLib.ScsMap.Map host,IFileSystem original,
        IReadOnlyList<string>? mods=null,CancellationToken token=default)
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
        return new(host,links);
    }
}
