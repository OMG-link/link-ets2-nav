using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinkEts2Nav.Runtime;

public sealed record SpeechPackSelection(long Revision,string Directory,
    IReadOnlyDictionary<string,string> Fixed,IReadOnlyDictionary<string,string> Names);
public sealed record SpeechPackSettingView(string ConfiguredDirectory,string ResolvedDirectory,string? ActiveDirectory);

/// <summary>Local user configuration; saving never copies, removes or synthesizes audio.</summary>
public sealed class SpeechPackSettings(string hostDirectory)
{
    public static SpeechPackSettings Current {get;}=new(AppContext.BaseDirectory);
    private const string DefaultDirectory="Assets/LinkEts2Nav/SpeechPack";
    private readonly SemaphoreSlim saving=new(1,1);
    private SpeechPackSelection? pending;
    private string? active;
    private long revision;
    private string ConfigFile=>Path.Combine(hostDirectory,"Settings","LinkEts2Nav.Speech.json");
    public SpeechPackSelection? Pending=>Volatile.Read(ref pending);
    internal string? ActiveDirectory => Volatile.Read(ref active);
    public void MarkActive(string? directory)=>Volatile.Write(ref active,directory);
    public SpeechPackSettingView Read()
    {
        string path=ReadDocument().Node["packDirectory"]?.GetValue<string>()??DefaultDirectory;
        return new(path,Path.GetFullPath(path,hostDirectory),ActiveDirectory);
    }
    public async Task<SpeechPackSelection> SaveAsync(string directory,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(directory)||directory.Any(char.IsControl))throw new ArgumentException("请填写预生成语音包目录。");
        directory=directory.Trim();
        string resolved=Path.GetFullPath(directory,hostDirectory);
        // Full checks run away from the UI dispatcher, before touching the old settings.
        var validated=await Task.Run(()=>Validate(resolved,token),token);
        await saving.WaitAsync(token);
        try
        {
            var original=ReadDocument();original.Node["packDirectory"]=directory;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            string temporary=ConfigFile+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary,original.Node.ToJsonString(new JsonSerializerOptions{WriteIndented=true}),token);
                string? current=File.Exists(ConfigFile)?await File.ReadAllTextAsync(ConfigFile,token):null;
                if(current!=original.Text)throw new IOException("语音配置已被其他操作修改，请重新保存。");
                File.Move(temporary,ConfigFile,true);
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
            var selection=new SpeechPackSelection(Interlocked.Increment(ref revision),resolved,validated.Fixed,validated.Names);
            Volatile.Write(ref pending,selection);
            return selection;
        }
        finally {saving.Release();}
    }
    private (JsonObject Node,string? Text) ReadDocument()
    {
        if(!File.Exists(ConfigFile))return(new(),null);
        if(new FileInfo(ConfigFile).Length>65536)throw new InvalidDataException("语音配置文件过大。");
        string text=File.ReadAllText(ConfigFile);
        return(JsonNode.Parse(text) as JsonObject??throw new InvalidDataException("语音配置格式无效。"),text);
    }
    private static (IReadOnlyDictionary<string,string> Fixed,IReadOnlyDictionary<string,string> Names) Validate(string directory, CancellationToken token)
    {
        if(!Directory.Exists(directory))throw new DirectoryNotFoundException("语音包目录不存在。");
        IReadOnlyDictionary<string,string> Load(string path,bool names=false)
        {
            var files=names ? PreGeneratedSpeech.LoadNames(path,cancellationToken:token) :
                PreGeneratedSpeech.Load(path,cancellationToken:token);
            if(files.Count==0 && File.Exists(Path.Combine(path,"manifest.json")))
                throw new InvalidDataException("语音包清单、音频或校验和无效："+path);
            return files;
        }
        var fixedFiles=Load(directory);var names=Load(Path.Combine(directory,"CityNames"),names:true);
        if(fixedFiles.Count+names.Count==0)throw new InvalidDataException("该目录没有可用的 manifest.json 或 CityNames/manifest.json。");
        return(fixedFiles,names);
    }
}
