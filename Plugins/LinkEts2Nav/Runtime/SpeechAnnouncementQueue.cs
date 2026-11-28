using System.Threading.Channels;
using System.Diagnostics;

namespace LinkEts2Nav.Runtime;

/// <summary>Shared text/audio cache with independent plans. Synthesis completion never plays audio.</summary>
public sealed class SpeechAnnouncementQueue : IAsyncDisposable
{
    private readonly ISpeechSynthesizer synthesizer;
    private readonly Action<string> play;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stop;
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object gate = new();
    private readonly Dictionary<string, string> ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> unavailable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Priority, string[] Texts)> plans = new(StringComparer.Ordinal);
    private readonly Task worker;
    private long audioVersion;
    private readonly SpeechUsageCollector? usage;
    private readonly HashSet<string> exactPack=new(StringComparer.Ordinal);
    public event Action? CacheChanged;
    public bool IsStopped => stop.IsCancellationRequested;

    public SpeechAnnouncementQueue(ISpeechSynthesizer synthesizer, Action<string> play,
        Action<string>? log = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? preGenerated = null,SpeechUsageCollector? usage=null)
    {
        this.usage=usage;
        this.synthesizer = synthesizer;
        this.play = play;
        this.log = log ?? (_ => { });
        if (preGenerated is not null)
            foreach (var entry in preGenerated){ready[entry.Key] = entry.Value;exactPack.Add(entry.Key);}
        usage?.SetExactPack(exactPack);
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(PrepareAsync);
    }
    /// <summary>Preparation faults are worker failures, not ordinary failed-text results.</summary>
    public void ThrowIfFaulted()
    {
        if(worker.IsCompleted)worker.GetAwaiter().GetResult();
    }

    public void UpdatePlan(string channel, IReadOnlyList<string> texts, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ThrowIfFaulted();
        if (IsStopped) return;
        string[] ordered = texts.Distinct(StringComparer.Ordinal).ToArray();
        bool updated;
        lock (gate)
        {
            updated = !plans.TryGetValue(channel, out var previous)
                || previous.Priority != priority || !previous.Texts.SequenceEqual(ordered);
            plans[channel] = (priority, ordered);
            foreach (string text in ready.Where(p => !File.Exists(p.Value)).Select(p => p.Key).ToArray())
            {
                ready.Remove(text);
                exactPack.Remove(text);usage?.SetExactPack(exactPack);
                updated = true;
            }
        }
        if (updated) changed.Writer.TryWrite(true);
    }

    public void ClearPlan(string channel) { lock (gate) plans.Remove(channel); }
    public void ReplacePreparedAudio(IReadOnlyDictionary<string,string> files,Action replaceSynthesizer)
    {
        ThrowIfFaulted();
        lock(gate)
        {
            replaceSynthesizer();audioVersion++;ready.Clear();exactPack.Clear();unavailable.Clear();
            foreach(var entry in files){ready[entry.Key]=entry.Value;exactPack.Add(entry.Key);}
            usage?.SetExactPack(exactPack);
        }
        changed.Writer.TryWrite(true);CacheChanged?.Invoke();
    }
    public bool IsReady(string text)
    {
        ThrowIfFaulted();
        lock (gate) return ready.TryGetValue(text, out string? file) && File.Exists(file);
    }
    internal bool IsUnavailable(string text)
    {
        ThrowIfFaulted();
        lock (gate) return unavailable.Contains(text);
    }

    public bool TryGetAudio(string text,out string file)
        =>TryGetAudio(text,out file,out _);
    public bool TryGetAudio(string text,out string file,out bool isExactPack)
    {
        ThrowIfFaulted();
        lock(gate)
        {
            if(ready.TryGetValue(text,out var candidate)&&File.Exists(candidate)){file=candidate;isExactPack=exactPack.Contains(text);return true;}
        }
        file="";isExactPack=false;return false;
    }

    public bool TryAnnounce(string text, string context = "预警")
    {
        ThrowIfFaulted();
        if (IsStopped) return false;
        string? file;
        lock (gate) ready.TryGetValue(text, out file);
        if (file is not null && File.Exists(file))
        {
            if (IsStopped) return false;
            play(file);
            log("TTS " + context + "已提交播放：" + text + " 文件=" + file);
            return true;
        }
        if (file is not null && !File.Exists(file))
        {
            lock (gate){ready.Remove(text);if(exactPack.Remove(text))usage?.SetExactPack(exactPack);}
            changed.Writer.TryWrite(true);
        }
        return false;
    }

    private async Task PrepareAsync()
    {
        var reportedModelFailures = new HashSet<bool>();
        try
        {
            while (await changed.Reader.WaitToReadAsync(stop.Token))
            {
                while (changed.Reader.TryRead(out _)) { }
                while (!stop.IsCancellationRequested)
                {
                    string? text;long version;
                    lock (gate){version=audioVersion;text = plans.OrderBy(p => p.Value.Priority).ThenBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value.Texts).FirstOrDefault(p => !ready.ContainsKey(p) && !unavailable.Contains(p));}
                    if (text is null) break;
                    var preparation=Stopwatch.StartNew();
                    log("开始准备语音：" + text);
                    string file;bool synthesized=false,cancelled=false;
                    try
                    {
                        file = await synthesizer.SynthesizeAsync(text, stop.Token);
                        synthesized=true;
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {cancelled=true;usage?.Prepared(text,preparation.Elapsed.TotalMilliseconds,null,cancelled:true);break;}
                    catch (SpeechModelUnavailableException issue)
                    {
                        lock (gate)
                        {
                            if (version != audioVersion) continue;
                            unavailable.Add(text);
                        }
                        if (reportedModelFailures.Add(issue.Unreadable))
                        {
                            log($"{issue.Message} 已安装的录音可继续使用，其余语句将播放提示音。");
                        }
                        CacheChanged?.Invoke();
                        continue;
                    }
                    finally
                    {
                        // Telemetry does not turn an exception into a normal failed-text result.
                        if(!synthesized&&!cancelled)usage?.Prepared(text,preparation.Elapsed.TotalMilliseconds,null,failed:true);
                    }
                    if (stop.IsCancellationRequested)
                    {usage?.Prepared(text,preparation.Elapsed.TotalMilliseconds,null,cancelled:true);break;}
                    bool stale;lock (gate){stale=version!=audioVersion;if(!stale){ready[text] = file;exactPack.Remove(text);}}
                    usage?.Prepared(text,preparation.Elapsed.TotalMilliseconds,stale?null:(synthesizer as ISpeechSynthesisDiagnostics)?.LastCacheHit,cancelled:stale);
                    if(stale)continue;
                    log("语音已就绪：" + text + " 文件=" + file);
                    CacheChanged?.Invoke();
                }
            }
        }
        catch (OperationCanceledException error) when (stop.IsCancellationRequested&&error.CancellationToken==stop.Token) { }
    }

    public async ValueTask DisposeAsync()
    {
        try{stop.Cancel();}
        finally
        {
            changed.Writer.TryComplete();
            try{await worker.ConfigureAwait(false);}
            finally
            {
                try{await synthesizer.DisposeAsync();}
                finally{stop.Dispose();}
            }
        }
    }
}
