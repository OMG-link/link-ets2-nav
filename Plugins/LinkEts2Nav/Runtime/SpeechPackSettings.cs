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
        IReadOnlyDictionary<string,string> Load(string path,int? limit=null)
        {
            if(!File.Exists(Path.Combine(path,"manifest.json")))return new Dictionary<string,string>();
            var files=PreGeneratedSpeech.Load(path, maximumEntries: limit, cancellationToken: token);
            if(files.Count==0)throw new InvalidDataException("语音包清单、音频或校验和无效："+path);
            foreach(string file in files.Values)
            {
                token.ThrowIfCancellationRequested();
                ValidateWave(file);
            }
            return files;
        }
        var fixedFiles=Load(directory);var names=Load(Path.Combine(directory,"CityNames"),4096);
        if(fixedFiles.Count+names.Count==0)throw new InvalidDataException("该目录没有可用的 manifest.json 或 CityNames/manifest.json。");
        return(fixedFiles,names);
    }
    private static void ValidateWave(string file)
    {
        using var input=File.OpenRead(file);using var reader=new BinaryReader(input);
        input.Position=12;bool format=false,data=false;
        while(input.Position+8<=input.Length)
        {
            string kind=System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));uint size=reader.ReadUInt32();long next=input.Position+size+(size%2);
            if(next>input.Length)throw new InvalidDataException("WAV 音频区块不完整。");
            if(kind=="fmt ")
            {
                if(size<16||reader.ReadUInt16()!=1||reader.ReadUInt16()!=1||reader.ReadUInt32()!=24000)
                    throw new InvalidDataException("语音包音频必须是24kHz单声道PCM16 WAV。");
                if(reader.ReadUInt32()!=48000||reader.ReadUInt16()!=2||reader.ReadUInt16()!=16)
                    throw new InvalidDataException("语音包音频必须是PCM16 WAV。");
                format=true;
            }
            if(kind=="data")data=size>0&&size%2==0;
            input.Position=next;
        }
        if(!format||!data)throw new InvalidDataException("WAV 音频缺少有效格式或采样数据。");
    }
}
