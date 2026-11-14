using System.Collections.Concurrent;
using System.Text;
using TruckLib;

namespace LinkEts2Nav.Map;

/// <summary>One immutable mounted-resource view. Native readers get independent read-only streams.</summary>
internal sealed class GameResourceStore(GameDataManager owner,Func<IFileSystem> source) : IFileSystem
{
    private readonly object io=new();
    private readonly Lazy<IFileSystem> source=new(source,LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ConcurrentDictionary<string,Lazy<byte[]>> bytes=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,Lazy<string[]>> directories=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,bool> filesExist=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,bool> directoriesExist=new(StringComparer.Ordinal);
    private long reads,scans,residentBytes;
    public GameDataManager Owner {get;}=owner;
    public char DirectorySeparator=>'/';
    public long ResourceReads=>Interlocked.Read(ref reads);
    public long DirectoryScans=>Interlocked.Read(ref scans);
    public long ResidentBytes=>Interlocked.Read(ref residentBytes);

    internal static string Key(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var parts=new List<string>();
        foreach(string part in path.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            if(part==".")continue;
            if(part=="..")
            {
                if(parts.Count==0)throw new ArgumentException("Resource path escapes its root.",nameof(path));
                parts.RemoveAt(parts.Count-1);
            }
            else parts.Add(part);
        }
        return "/"+string.Join('/',parts);
    }
    private byte[] Buffer(string path)=>bytes.GetOrAdd(Key(path),p=>new(()=>
    {
        byte[] value;
        lock(io){Interlocked.Increment(ref reads);value=source.Value.ReadAllBytes(p);}
        Interlocked.Add(ref residentBytes,value.Length);
        return value;
    },LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    internal ReadOnlyMemory<byte> Read(string path)=>Buffer(path);
    public byte[] ReadAllBytes(string path)=>Buffer(path).ToArray();
    public Stream Open(string path)=>new MemoryStream(Buffer(path),false);
    public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
    public string ReadAllText(string path,Encoding encoding)
    {using var reader=new StreamReader(Open(path),encoding,true);return reader.ReadToEnd();}
    public bool FileExists(string path)=>filesExist.GetOrAdd(Key(path),p=>{lock(io)return source.Value.FileExists(p);});
    public bool DirectoryExists(string path)=>directoriesExist.GetOrAdd(Key(path),p=>{lock(io)return source.Value.DirectoryExists(p);});
    public IList<string> GetFiles(string path)=>Array.AsReadOnly(directories.GetOrAdd(Key(path),p=>new(()=>
    {
        lock(io){Interlocked.Increment(ref scans);return source.Value.GetFiles(p).Select(Key).Distinct(StringComparer.Ordinal).ToArray();}
    },LazyThreadSafetyMode.ExecutionAndPublication)).Value);
    public string GetParent(string path)
    {string key=Key(path);int slash=key.LastIndexOf('/');return slash==0?"/":key[..slash];}
}
