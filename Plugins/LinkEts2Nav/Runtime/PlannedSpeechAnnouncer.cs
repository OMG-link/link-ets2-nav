using System.Threading.Channels;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Runtime;

public interface IWarningPlanner
{
    void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now);
    void ClearPlan();
}

/// <summary>Whole sentences are prepared in trigger order; completion never causes late playback.</summary>
public sealed class PlannedSpeechAnnouncer : IWarningAnnouncer, IWarningPlanner, IAsyncDisposable
{
    private readonly ISpeechSynthesizer synthesizer;
    private readonly IWarningAnnouncer fallback;
    private readonly Action<string> play;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stop;
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object gate = new();
    private readonly Dictionary<string, string> ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private string[] pending = [];
    private readonly Task worker;

    public PlannedSpeechAnnouncer(ISpeechSynthesizer synthesizer, IWarningAnnouncer fallback,
        Action<string> play, Action<string>? log = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? preGenerated = null)
    {
        if (preGenerated is not null)
            foreach (var entry in preGenerated) ready[entry.Key] = entry.Value;
        this.synthesizer = synthesizer;
        this.fallback = fallback;
        this.play = play;
        this.log = log ?? (_ => { });
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(PrepareAsync);
    }

    public static string Text(double stage, double actualDistance, bool firstConfirmedInside200Meters = false, int? speedLimitKph = null)
    {
        string text = BaseText(stage, actualDistance, firstConfirmedInside200Meters);
        return speedLimitKph is > 0 and <= 300 ? text.TrimEnd('。') + "，限速" + Number(speedLimitKph.Value) + "公里。" : text;
    }

    public static string Number(int value)
    {
        if (value is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(value));
        const string digits = "零一二三四五六七八九";
        string result = value >= 100 ? digits[value / 100] + "百" : "";
        int rest = value % 100;
        if (value >= 100 && rest is > 0 and < 10) result += "零";
        if (rest >= 10) result += (rest / 10 == 1 && value < 100 ? "" : digits[rest / 10].ToString()) + "十";
        if (rest % 10 != 0) result += digits[rest % 10];
        return result;
    }

    private static string BaseText(double stage, double actualDistance, bool firstConfirmedInside200Meters)
    {
        if (stage == 200) return firstConfirmedInside200Meters
            ? "二百米内，有测速照相。" : "二百米处，有测速照相。";
        // A late first observation must not claim that a camera is still 500/200 m away.
        if (actualDistance < stage * .9) return "前方有测速照相。";
        return stage == 500 ? "五百米处，有测速照相。" :
            stage == 200 ? "二百米处，有测速照相。" : "前方有测速照相。";
    }

    public void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now)
    {
        var age = snapshot is null ? TimeSpan.MaxValue : now - snapshot.ObservedAt;
        if (stop.IsCancellationRequested || snapshot is not { IsActive: true } || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
        { ClearPlan(); return; }
        var ordered = plans.OrderBy(p => p.DistanceUntilTriggerMeters)
            .Select(p => Text(p.StageDistanceMeters, Math.Min(p.DistanceMeters, p.StageDistanceMeters), p.FirstConfirmedInside200Meters, p.SpeedLimitKph))
            .Distinct(StringComparer.Ordinal).ToArray();
        bool updated;
        lock (gate)
        {
            updated = !pending.SequenceEqual(ordered);
            pending = ordered;
            foreach (var text in ready.Where(p => !File.Exists(p.Value)).Select(p => p.Key).ToArray())
            {
                ready.Remove(text);
                updated = true;
            }
        }
        if (updated) changed.Writer.TryWrite(true);
    }

    public void ClearPlan() { lock (gate) pending = []; }

    public void Announce(CameraWarning warning)
    {
        if (stop.IsCancellationRequested) return;
        string text = Text(warning.StageDistanceMeters, warning.DistanceMeters, warning.FirstConfirmedInside200Meters, warning.SpeedLimitKph);
        string? file;
        lock (gate) ready.TryGetValue(text, out file);
        if ((file is null || !File.Exists(file)) && warning.SpeedLimitKph.HasValue)
        {
            string basic = Text(warning.StageDistanceMeters, warning.DistanceMeters, warning.FirstConfirmedInside200Meters);
            lock (gate) ready.TryGetValue(basic, out file);
            if (file is not null && File.Exists(file))
            {
                log("带限速语音尚未就绪，使用对应的不含限速版本：" + text);
                text = basic;
            }
        }
        if (file is not null && File.Exists(file))
        {
            try
            {
                if (stop.IsCancellationRequested) return;
                play(file);
                log("TTS 预警已提交播放：" + text + " 文件=" + file);
                return;
            }
            catch (Exception ex) { log("TTS 音频提交失败，使用现有提示音：" + ex.Message); }
        }
        if (file is not null && !File.Exists(file))
        {
            lock (gate) ready.Remove(text);
            changed.Writer.TryWrite(true);
        }
        log("TTS 缓存尚未就绪，立即使用现有提示音；不延迟补播：" + text);
        if (!stop.IsCancellationRequested) fallback.Announce(warning);
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
                    lock (gate) text = pending.FirstOrDefault(p => !ready.ContainsKey(p) && !failed.Contains(p));
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
