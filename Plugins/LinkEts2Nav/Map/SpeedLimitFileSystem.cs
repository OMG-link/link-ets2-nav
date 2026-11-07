using System.IO.Compression;
using System.Text;
using TruckLib;
namespace LinkEts2Nav.Map;

/// <summary>Read-only definition overlay; geometric resources continue to use the approved official mount.</summary>
internal sealed class SpeedLimitFileSystem(IFileSystem original, Dictionary<string,byte[]> overrides) : IFileSystem
{
    public char DirectorySeparator => '/';
    private static string Key(string path) => "/" + path.Replace('\\','/').Trim('/');
    public bool FileExists(string path) => overrides.ContainsKey(Key(path)) || original.FileExists(path);
    public bool DirectoryExists(string path) => overrides.Keys.Any(p=>p.StartsWith(Key(path)+"/",StringComparison.Ordinal)) || original.DirectoryExists(path);
    public IList<string> GetFiles(string path) => original.GetFiles(path).Concat(overrides.Keys.Where(p=>
        p.StartsWith(Key(path)+"/",StringComparison.Ordinal))).Distinct(StringComparer.Ordinal).ToList();
    public byte[] ReadAllBytes(string path) => overrides.TryGetValue(Key(path),out var bytes) ? bytes.ToArray() : original.ReadAllBytes(path);
    public string ReadAllText(string path) => ReadAllText(path,Encoding.UTF8);
    public string ReadAllText(string path,Encoding encoding)
    {
        using var stream=Open(path); using var reader=new StreamReader(stream,encoding,true);
        return reader.ReadToEnd();
    }
    public Stream Open(string path) => overrides.TryGetValue(Key(path),out var bytes) ? new MemoryStream(bytes,false) : original.Open(path);
    public string GetParent(string path) => original.GetParent(path);

    public static IFileSystem Create(IFileSystem original,IReadOnlyList<string> mods)
    {
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        long total=0;
        void Add(string name,Stream stream)
        {
            string key=Key(name);
            if(!key.StartsWith("/def/",StringComparison.Ordinal) ||
                !(key.EndsWith(".sii",StringComparison.Ordinal)||key.EndsWith(".sui",StringComparison.Ordinal))) return;
            using var memory=new MemoryStream();
            byte[] buffer=new byte[8192]; int count;
            while((count=stream.Read(buffer,0,buffer.Length))>0)
            {
                memory.Write(buffer,0,count);
                if(memory.Length>4*1024*1024) throw new InvalidDataException("Mod definition exceeds 4 MiB.");
            }
            total+=memory.Length;
            if(total>64*1024*1024) throw new InvalidDataException("Mod definition overlay exceeds 64 MiB.");
            files[key]=memory.ToArray();
        }
        // Game log mount order is low to high priority, as with the official AssetLoader.
        foreach(string mod in mods)
        {
            if(Directory.Exists(mod))
            {
                string root=Path.Combine(mod,"def");
                if(!Directory.Exists(root)) continue;
                foreach(string path in Directory.EnumerateFiles(root,"*",new EnumerationOptions { RecurseSubdirectories=true, AttributesToSkip=FileAttributes.ReparsePoint }))
                {
                    if(Path.GetExtension(path) is not (".sii" or ".sui")) continue;
                    using var stream=File.OpenRead(path); Add(Path.GetRelativePath(mod,path),stream);
                }
            }
            else
            {
                try
                {
                    using var zip=ZipFile.OpenRead(mod);
                    foreach(var entry in zip.Entries.Where(e=>e.FullName.Replace('\\','/').StartsWith("def/",StringComparison.Ordinal)))
                    { using var stream=entry.Open(); Add(entry.FullName,stream); }
                }
                catch(InvalidDataException)
                {
                    throw new NotSupportedException("限速暂不支持该模组归档的定义覆盖："+Path.GetFileName(mod));
                }
            }
        }
        return new SpeedLimitFileSystem(original,files);
    }
}
