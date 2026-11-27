using System.Collections.Concurrent;
using System.Text;
using TruckLib;

namespace LinkEts2Nav.Map;

/// <summary>One immutable mounted-resource view. Native readers get independent read-only streams.</summary>
internal sealed class GameResourceStore(GameDataManager owner,Func<IFileSystem> source,string mapPath,
    int rawFileBudget=GameResourceStore.DefaultRawFileBudget) : IFileSystem
{
    internal const int DefaultRawFileBudget=32*1024*1024;
    private readonly record struct CachedFile(string Path,int Size);
    private readonly object io=new();
    private readonly Lazy<IFileSystem> source=new(source,LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ConcurrentDictionary<string,Lazy<byte[]>> bytes=new(StringComparer.Ordinal);
    // TruckLib accepts either an MBD or a sector directory (including a custom mod map).
    private readonly string mapDirectory=Key(mapPath).TrimEnd('/')+"/";
    private readonly string mbdSectorDirectory=Path.ChangeExtension(Key(mapPath),null)!.TrimEnd('/')+"/";
    private readonly Dictionary<string,LinkedListNode<CachedFile>> retainedFiles=new(StringComparer.Ordinal);
    private readonly LinkedList<CachedFile> recency=new();
    private long boundedBytes;
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
    private bool HasByteBudget(string key)
    {
        var extension=Path.GetExtension(key.AsSpan());
        if(extension.Equals(".pmg",StringComparison.OrdinalIgnoreCase))return true;
        if(extension is not (".base" or ".aux" or ".data" or ".desc" or ".snd" or ".layer"))return false;
        var directory=key.AsSpan(0,key.LastIndexOf('/')+1);
        return directory.SequenceEqual(mapDirectory)||directory.SequenceEqual(mbdSectorDirectory);
    }

    private byte[] Buffer(string path)
    {
        string key=Key(path);
        bool bounded=HasByteBudget(key);
        var entry=bytes.GetOrAdd(key,(p,hasBudget)=>new(()=>
        {
            byte[] value;
            lock(io){Interlocked.Increment(ref reads);value=source.Value.ReadAllBytes(p);}
            if(!hasBudget)Interlocked.Add(ref residentBytes,value.Length);
            return value;
        },LazyThreadSafetyMode.ExecutionAndPublication),bounded);
        var buffer=entry.Value;
        if(bounded)RetainRecent(key,entry,buffer.Length);
        return buffer;
    }

    private void RetainRecent(string key,Lazy<byte[]> entry,int size)
    {
        lock(retainedFiles)
        {
            // A concurrent reader may still hold the old buffer after another read evicted it.
            if(!bytes.TryGetValue(key,out var current)||!ReferenceEquals(entry,current))return;
            if(retainedFiles.TryGetValue(key,out var hit))
            {
                recency.Remove(hit);
                recency.AddFirst(hit);
                return;
            }
            if(size==0||size>rawFileBudget)
            {
                bytes.TryRemove(key,out _);
                return;
            }
            long change=size;
            while(boundedBytes+change>rawFileBudget)
            {
                var oldest=recency.Last!.Value;
                recency.RemoveLast();
                retainedFiles.Remove(oldest.Path);
                bytes.TryRemove(oldest.Path,out _);
                change-=oldest.Size;
            }
            retainedFiles.Add(key,recency.AddFirst(new CachedFile(key,size)));
            boundedBytes+=change;
            Interlocked.Add(ref residentBytes,change);
        }
    }
    internal ReadOnlyMemory<byte> Read(string path)=>Buffer(path);
    internal IFileSystem SequentialMapReads()=>new SequentialMapReader(this);

    // A one-pass index or far-route read can share hot bytes without evicting the driving window.
    private byte[] ReadWithoutRetaining(string path)
    {
        string key=Key(path);
        if(bytes.TryGetValue(key,out var entry)&&entry.IsValueCreated)return entry.Value;
        lock(io)
        {
            Interlocked.Increment(ref reads);
            return source.Value.ReadAllBytes(key);
        }
    }

    private sealed class SequentialMapReader(GameResourceStore owner) : IFileSystem
    {
        public char DirectorySeparator=>owner.DirectorySeparator;
        public Stream Open(string path)=>new MemoryStream(owner.ReadWithoutRetaining(path),false);
        public byte[] ReadAllBytes(string path)=>owner.ReadWithoutRetaining(path).ToArray();
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)
        {using var reader=new StreamReader(Open(path),encoding,true);return reader.ReadToEnd();}
        public bool FileExists(string path)=>owner.FileExists(path);
        public bool DirectoryExists(string path)=>owner.DirectoryExists(path);
        public IList<string> GetFiles(string path)=>owner.GetFiles(path);
        public string GetParent(string path)=>owner.GetParent(path);
    }

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
