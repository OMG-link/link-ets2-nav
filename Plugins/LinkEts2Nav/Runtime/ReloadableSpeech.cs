namespace LinkEts2Nav.Runtime;

/// <summary>Swaps only speech resources; map, route progress and trigger consumption remain intact.</summary>
public sealed class ReloadableSpeech:ISpeechSynthesizer,ISpeechSynthesisDiagnostics
{
    public bool? LastCacheHit {get{lock(gate)return(worker as ISpeechSynthesisDiagnostics)?.LastCacheHit;}}
    private readonly SpeechOptions options;
    private readonly SpeechPackSettings settings;
    private readonly Func<SpeechOptions,ISpeechSynthesizer> create;
    private readonly Action<string> log;
    private readonly object gate=new();
    private ISpeechSynthesizer worker;
    private long revision;
    public HashSet<string> Names {get;}
    public IReadOnlyDictionary<string,string> Fixed {get;}
    public ReloadableSpeech(SpeechOptions options,SpeechPackSettings settings,Action<string>? log=null,
        Func<SpeechOptions,ISpeechSynthesizer>? create=null)
    {
        this.options=options;this.settings=settings;this.create=create??(value=>new KokoroSpeechProcess(value));this.log=log??(_=>{});
        worker=this.create(options);
        Fixed=PreGeneratedSpeech.Load(options.PackDirectory,log);
        Names=PreGeneratedSpeech.Load(Path.Combine(options.PackDirectory,"CityNames"),log,4096).Keys.ToHashSet(StringComparer.Ordinal);
        revision=settings.Pending is {} saved&&Path.GetFullPath(saved.Directory)==Path.GetFullPath(options.PackDirectory)?saved.Revision:0;
        settings.MarkActive(options.PackDirectory);
    }
    public Task<string> SynthesizeAsync(string text,CancellationToken token)
    {lock(gate)return worker.SynthesizeAsync(text,token);}
    public async Task<bool> ApplyPendingAsync(SpeechAnnouncementQueue queue)
    {
        if(settings.Pending is not {} selection||selection.Revision<=revision)return false;
        var next=create(new SpeechOptions{Enabled=options.Enabled,ModelDirectory=options.ModelDirectory,
            WorkerPath=options.WorkerPath,CacheDirectory=options.CacheDirectory,Voice=options.Voice,
            Threads=options.Threads,PackDirectory=selection.Directory});
        ISpeechSynthesizer? previous=null;
        queue.ReplacePreparedAudio(selection.Fixed,()=>{lock(gate){previous=worker;worker=next;}});
        Names.Clear();Names.UnionWith(selection.Names.Keys);
        revision=selection.Revision;settings.MarkActive(selection.Directory);
        try{await previous!.DisposeAsync();}
        catch(Exception ex){log("旧语音工作进程清理失败："+ex.Message);}
        return true;
    }
    public async ValueTask DisposeAsync()
    {settings.MarkActive(null);await worker.DisposeAsync();}
}
