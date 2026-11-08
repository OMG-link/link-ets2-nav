using LinkEts2Nav.Core;

namespace LinkEts2Nav.Runtime;

public interface IWarningPlanner
{
    void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now);
    void ClearPlan();
}

/// <summary>Camera-specific wording and fallback policy over a shared speech queue.</summary>
public sealed class PlannedSpeechAnnouncer : IWarningAnnouncer, IWarningPlanner, IAsyncDisposable
{
    private readonly IWarningAnnouncer fallback;
    private readonly Action<string> log;
    private readonly bool ownsQueue;
    public SpeechAnnouncementQueue Queue { get; }

    public PlannedSpeechAnnouncer(ISpeechSynthesizer synthesizer, IWarningAnnouncer fallback,
        Action<string> play, Action<string>? log = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? preGenerated = null)
        : this(new SpeechAnnouncementQueue(synthesizer, play, log, cancellationToken, preGenerated), fallback, log)
    { ownsQueue = true; }

    public PlannedSpeechAnnouncer(SpeechAnnouncementQueue queue, IWarningAnnouncer fallback, Action<string>? log = null)
    {
        Queue = queue;
        this.fallback = fallback;
        this.log = log ?? (_ => { });
    }

    public const string UncalibratedText = "正靠近未校准的探头，无法获取其监测方向，请减速慢行。";

    public static string Text(double stage, double actualDistance, bool firstConfirmedInside200Meters = false, int? speedLimitKph = null, CameraWarningKind kind = CameraWarningKind.Calibrated)
    {
        if (kind == CameraWarningKind.UncalibratedProximity) return UncalibratedText;
        string text = BaseText(stage, actualDistance, firstConfirmedInside200Meters);
        return speedLimitKph is > 0 and <= 300 ? text.TrimEnd('。') + "，限速" + Number(speedLimitKph.Value) + "。" : text;
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
            ? "两百米内，有测速照相。" : "两百米处，有测速照相。";
        // A late first observation must not claim that a camera is still 500/200 m away.
        if (actualDistance < stage * .9) return "前方有测速照相。";
        return stage == 500 ? "五百米处，有测速照相。" :
            stage == 200 ? "两百米处，有测速照相。" : "前方有测速照相。";
    }

    public void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now)
    {
        var age = snapshot is null ? TimeSpan.MaxValue : now - snapshot.ObservedAt;
        if (Queue.IsStopped || snapshot is not { IsActive: true } || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
        { ClearPlan(); return; }
        var ordered = plans.OrderBy(p => p.DistanceUntilTriggerMeters)
            .Select(p => Text(p.StageDistanceMeters, Math.Min(p.DistanceMeters, p.StageDistanceMeters),
                p.FirstConfirmedInside200Meters, p.SpeedLimitKph, p.Kind)).ToArray();
        Queue.UpdatePlan("camera", ordered);
    }

    public void ClearPlan() => Queue.ClearPlan("camera");

    public void Announce(CameraWarning warning)
    {
        if (Queue.IsStopped) return;
        string text = Text(warning.StageDistanceMeters, warning.DistanceMeters,
            warning.FirstConfirmedInside200Meters, warning.SpeedLimitKph, warning.Kind);
        if (!Queue.IsReady(text) && warning.SpeedLimitKph.HasValue)
        {
            string basic = Text(warning.StageDistanceMeters, warning.DistanceMeters, warning.FirstConfirmedInside200Meters);
            if (Queue.IsReady(basic))
            {
                log("带限速语音尚未就绪，使用对应的不含限速版本：" + text);
                text = basic;
            }
        }
        if (Queue.TryAnnounce(text)) return;
        log("TTS 缓存尚未就绪，立即使用现有提示音；不延迟补播：" + text);
        if (!Queue.IsStopped) fallback.Announce(warning);
    }

    public async ValueTask DisposeAsync()
    {
        ClearPlan();
        if (ownsQueue) await Queue.DisposeAsync();
    }
}