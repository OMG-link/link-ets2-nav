using System.Threading.Channels;

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
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Priority, string[] Texts)> plans = new(StringComparer.Ordinal);
    private readonly Task worker;
    public bool IsStopped => stop.IsCancellationRequested;

    public SpeechAnnouncementQueue(ISpeechSynthesizer synthesizer, Action<string> play,
        Action<string>? log = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? preGenerated = null)
    {
        this.synthesizer = synthesizer;
        this.play = play;
        this.log = log ?? (_ => { });
        if (preGenerated is not null)
            foreach (var entry in preGenerated) ready[entry.Key] = entry.Value;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(PrepareAsync);
    }

    public void UpdatePlan(string channel, IReadOnlyList<string> texts, int priority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
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
                updated = true;
            }
        }
        if (updated) changed.Writer.TryWrite(true);
    }

    public void ClearPlan(string channel) { lock (gate) plans.Remove(channel); }
    public bool IsReady(string text)
    {
        lock (gate) return ready.TryGetValue(text, out string? file) && File.Exists(file);
    }

    public bool HasFailed(string text) { lock (gate) return failed.Contains(text); }

    public bool TryAnnounce(string text, string context = "预警")
    {
        if (IsStopped) return false;
        string? file;
        lock (gate) ready.TryGetValue(text, out file);
        if (file is not null && File.Exists(file))
        {
            try
            {
                if (IsStopped) return false;
                play(file);
                log("TTS " + context + "已提交播放：" + text + " 文件=" + file);
                return true;
            }
            catch (Exception ex) { log("TTS 音频提交失败，使用现有提示音：" + ex.Message); }
        }
        if (file is not null && !File.Exists(file))
        {
            lock (gate) ready.Remove(text);
            changed.Writer.TryWrite(true);
        }
        return false;
    }

    private async Task PrepareAsync()
    {
        try
        {
            while (await changed.Reader.WaitToReadAsync(stop.Token))
            {
                while (changed.Reader.TryRead(out _)) { }
                while (!stop.IsCancellationRequested)
                {
                    string? text;
                    lock (gate) text = plans.OrderBy(p => p.Value.Priority).ThenBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value.Texts).FirstOrDefault(p => !ready.ContainsKey(p) && !failed.Contains(p));
                    if (text is null) break;
                    try
                    {
                        log("开始准备 TTS 音频：" + text);
                        string file = await synthesizer.SynthesizeAsync(text, stop.Token);
                        if (stop.IsCancellationRequested) break;
                        lock (gate) ready[text] = file;
                        log("TTS 文件缓存已就绪：" + text + " 文件=" + file);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        lock (gate) failed.Add(text);
                        log("TTS 合成失败，本次启用期间使用现有提示音：" + ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        changed.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        await synthesizer.DisposeAsync();
        stop.Dispose();
    }
}
