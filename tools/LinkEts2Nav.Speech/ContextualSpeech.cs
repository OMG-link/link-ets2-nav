using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Speech;

public sealed record ContextFragment(string Text,string CarrierText,string[] FilledWords,int StartChar,int EndChar);
public sealed record ContextPlan(int Schema,ContextFragment[] Items);
public sealed record TimestampOutput(JsonElement Output,long ReceivedSamples);
public sealed record ContextTimestamps(int Schema,string SourceText,string PcmSha256,TimestampOutput[] Events);
public sealed record ContextSlice(int StartSample,int EndSample,string Method);

/// <summary>Generate complete carrier sentences, then extract fragments at verified word boundaries.</summary>
public static class ContextualSpeech
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented=true };
    public static ContextPlan ReadPlan(string? file=null)
    {
        using var stream=file is null?typeof(ContextualSpeech).Assembly.GetManifestResourceStream("LinkEts2Nav.Speech.contextual-fragments.json")!
            :File.OpenRead(file);
        var plan=JsonSerializer.Deserialize<ContextPlan>(stream);
        if(plan is not {Schema:1,Items:not null} || plan.Items.Length is <1 or >256
            ||plan.Items.Any(f=>f is null)||plan.Items.Select(f=>f.Text).Distinct(StringComparer.Ordinal).Count()!=plan.Items.Length)
            throw new InvalidDataException("Invalid contextual fragment plan.");
        foreach(var f in plan.Items)
            if(string.IsNullOrWhiteSpace(f.Text)||string.IsNullOrWhiteSpace(f.CarrierText)||f.CarrierText.Length>2048
                ||f.StartChar<0||f.EndChar>f.CarrierText.Length||f.EndChar<=f.StartChar
                ||f.CarrierText[f.StartChar..f.EndChar]!=f.Text||f.FilledWords is null||f.FilledWords.Length==0
                ||f.FilledWords.Any(w=>string.IsNullOrWhiteSpace(w)||!f.CarrierText.Contains(w,StringComparison.Ordinal)))
                throw new InvalidDataException("Fragment text must match a real range of its complete carrier sentence.");
        return plan;
    }

    public static ContextSlice Locate(ContextFragment fragment,ContextTimestamps timestamps,byte[] pcm)
    {
        if(timestamps.Schema!=1||timestamps.SourceText!=fragment.CarrierText||timestamps.PcmSha256!=SpeechPack.Hash(pcm)
            ||timestamps.Events is null)throw new InvalidDataException("Carrier audio/timestamp provenance mismatch.");
        var ends=timestamps.Events.Where(e=>e.Output.TryGetProperty("type",out var type)&&type.GetString()=="sentence-end").ToArray();
        if(ends.Length!=1)throw new InvalidDataException("Expected one timed carrier sentence; inspect preserved events before slicing multiple sentences.");
        var output=ends[0].Output;
        if(output.GetProperty("original_text").GetString()!=fragment.CarrierText)
            throw new InvalidDataException("Provider carrier text differs from requested text.");
        var sentence=output.GetProperty("sentence");
        if(sentence.GetProperty("index").GetInt32()!=0)throw new InvalidDataException("Unsupported carrier sentence index.");
        var covered=new bool[fragment.CarrierText.Length];
        int? first=null,last=null;int previousIndex=0,previousTime=0,textCursor=0;
        foreach(var word in sentence.GetProperty("words").EnumerateArray())
        {
            int beginIndex=word.GetProperty("begin_index").GetInt32(),endIndex=word.GetProperty("end_index").GetInt32();
            string text=word.GetProperty("text").GetString()??"";
            // Provider indices count spoken characters, while text can attach punctuation
            // to the next character. Recover raw text offsets by an exact sequential match.
            int a=textCursor,b=checked(a+text.Length);
            int begin=word.GetProperty("begin_time").GetInt32(),end=word.GetProperty("end_time").GetInt32();
            if(beginIndex<previousIndex||endIndex<=beginIndex||b<=a||b>fragment.CarrierText.Length||begin<previousTime||end<=begin
                ||begin<0||text!=fragment.CarrierText[a..b])
                throw new InvalidDataException("Non-monotone or mismatched provider word alignment.");
            previousIndex=endIndex;previousTime=end;textCursor=b;
            if(b<=fragment.StartChar||a>=fragment.EndChar)continue;
            if(a<fragment.StartChar||b>fragment.EndChar)throw new InvalidDataException("Fragment boundary would split a timed word.");
            first??=checked(begin*24);last=checked(end*24);
            for(int i=a;i<b;i++)covered[i]=true;
        }
        if(textCursor!=fragment.CarrierText.Length)throw new InvalidDataException("Provider words do not reconstruct the complete carrier text.");
        if(first is null||last is null)throw new InvalidDataException("No word timestamps for fragment.");
        for(int i=fragment.StartChar;i<fragment.EndChar;i++)
            if(char.IsLetterOrDigit(fragment.CarrierText[i])&&!covered[i])throw new InvalidDataException("Incomplete fragment word coverage.");
        int start=fragment.StartChar==0?0:first.Value,endSample=fragment.EndChar==fragment.CarrierText.Length?pcm.Length/2:last.Value;
        if(start<0||endSample<=start||endSample>pcm.Length/2)throw new InvalidDataException("Word timestamps exceed source PCM duration.");
        return new(start,endSample,"qwen-word-timestamps");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var options=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int i=1;i<args.Length;i+=2)
            if(i+1>=args.Length||args[i] is not ("--plan" or "--output" or "--keys" or "--carriers")
                ||!options.TryAdd(args[i],args[i+1]))throw new ArgumentException("Unknown, repeated or incomplete context option.");
        string Required(string key)=>options.GetValueOrDefault(key)??throw new ArgumentException("Missing "+key);
        var plan=ReadPlan(options.GetValueOrDefault("--plan"));
        string output=Path.GetFullPath(Required("--output"));Directory.CreateDirectory(output);
        string[] carriers=plan.Items.Select(f=>f.CarrierText).Distinct(StringComparer.Ordinal).ToArray();
        if(args[0]=="--export-context-jobs")
        {
            await File.WriteAllTextAsync(Path.Combine(output,"contextual-fragments.json"),JsonSerializer.Serialize(plan,Json));
            await File.WriteAllLinesAsync(Path.Combine(output,"carrier-texts.txt"),carriers,new UTF8Encoding(false));
            var fragments=plan.Items.Select(f=>f.Text).ToHashSet(StringComparer.Ordinal);
            await File.WriteAllLinesAsync(Path.Combine(output,"direct-texts.txt"),SpeechTextCatalog.DefaultTexts.Where(t=>!fragments.Contains(t)),new UTF8Encoding(false));
            Console.WriteLine($"Context fragments: {plan.Items.Length}; complete carrier sentences: {carriers.Length}.");return 0;
        }
        string sourceDirectory=Path.GetFullPath(options.GetValueOrDefault("--carriers")??Path.Combine(output,"carriers"));
        if(string.Equals(sourceDirectory,output,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new ArgumentException("Carrier and fragment directories must differ.");
        if(args[0]=="--generate-context-pack")
        {
            string key=await QwenPackGenerator.ReadKeyAsync(Required("--keys"));
            await SpeechPackBuilder.BuildAsync(sourceDirectory,QwenPackGenerator.Model,QwenPackGenerator.Voice,carriers,
                async(text,token)=>
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var events=new List<TimestampOutput>();
                    byte[] pcm=await QwenPackGenerator.GenerateAsync(key,text,timeout.Token,timestamps:true,
                        observeOutput:(item,samples)=>events.Add(new(item,samples)));
                    await File.WriteAllTextAsync(Path.Combine(sourceDirectory,SpeechPack.FileName(text)+".timestamps.json"),
                        JsonSerializer.Serialize(new ContextTimestamps(1,text,SpeechPack.Hash(pcm),events.ToArray()),Json),token);
                    return pcm;
                },Console.WriteLine);
        }
        else if(args[0]!="--slice-context-pack")throw new ArgumentException("Unknown contextual speech command.");
        var sourcePack=JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(Path.Combine(sourceDirectory,"manifest.json")))
            ??throw new InvalidDataException("Missing carrier manifest.");
        var samples=new Dictionary<string,byte[]>(StringComparer.Ordinal);var provenance=new List<object>();
        foreach(var item in plan.Items)
        {
            var source=sourcePack.Entries.Single(e=>e.Text==item.CarrierText);
            if(source.File!=SpeechPack.FileName(item.CarrierText))throw new InvalidDataException("Unexpected carrier filename.");
            string path=Path.Combine(sourceDirectory,source.File);
            string sourceHash=SpeechPack.Hash(await File.ReadAllBytesAsync(path));
            if(sourceHash!=source.Sha256)throw new InvalidDataException("Carrier WAV checksum mismatch.");
            byte[] pcm=PcmAudio.Read(path);
            var times=JsonSerializer.Deserialize<ContextTimestamps>(await File.ReadAllTextAsync(path+".timestamps.json"))
                ??throw new InvalidDataException("Missing carrier timestamps.");
            var cut=Locate(item,times,pcm);
            samples.Add(item.Text,pcm[(cut.StartSample*2)..(cut.EndSample*2)]);
            provenance.Add(new{item.Text,item.CarrierText,item.FilledWords,item.StartChar,item.EndChar,SourceFile=source.File,
                SourceSha256=sourceHash,cut.StartSample,cut.EndSample,SampleRate=24000,cut.Method,Model=sourcePack.Model,Voice=sourcePack.Voice});
        }
        await SpeechPackBuilder.BuildAsync(output,sourcePack.Model,sourcePack.Voice,plan.Items.Select(i=>i.Text),
            (text,_)=>Task.FromResult(samples[text]),Console.WriteLine,reuseCachedAudio:false);
        await File.WriteAllTextAsync(Path.Combine(output,"context-provenance.json"),JsonSerializer.Serialize(provenance,Json));
        Console.WriteLine("Context fragments ready: "+plan.Items.Length);return 0;
    }
}
