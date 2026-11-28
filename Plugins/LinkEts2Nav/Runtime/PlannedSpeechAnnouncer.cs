using LinkEts2Nav.Core;

namespace LinkEts2Nav.Runtime;

public interface IWarningPlanner
{
    void UpdatePlan(WarningSnapshot? snapshot, IReadOnlyList<PlannedCameraWarning> plans, DateTimeOffset now);
    void ClearPlan();
}

/// <summary>Camera-specific preparation and wording over a shared speech queue.</summary>
public sealed class PlannedSpeechAnnouncer : IWarningPlanner, IAsyncDisposable
{
    private readonly bool ownsQueue;
    public SpeechAnnouncementQueue Queue { get; }

    public PlannedSpeechAnnouncer(ISpeechSynthesizer synthesizer,
        Action<string> play, Action<string>? log = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? preGenerated = null,SpeechUsageCollector? usage=null)
        : this(new SpeechAnnouncementQueue(synthesizer, play, log, cancellationToken, preGenerated,usage))
    { ownsQueue = true; }

    public PlannedSpeechAnnouncer(SpeechAnnouncementQueue queue)=>Queue=queue;

    public const string UncalibratedText = "正靠近未校准的探头，无法获取其监测方向，请减速慢行。";

    public static string Text(double stage, double actualDistance, bool firstConfirmedInside200Meters = false, int? speedLimitKph = null, CameraWarningKind kind = CameraWarningKind.Calibrated)
    {
        if (kind == CameraWarningKind.UncalibratedProximity) return UncalibratedText;
        string text = BaseText(stage, actualDistance, firstConfirmedInside200Meters);
        return speedLimitKph is > 0 and <= 300 ? text.TrimEnd('。') + "，限速" + Number(speedLimitKph.Value) + "。" : text;
    }

    public static string DistanceText(int meters,int? speedLimitKph=null,CameraWarningKind kind=CameraWarningKind.Calibrated)
    {
        if(kind==CameraWarningKind.UncalibratedProximity)return UncalibratedText;
        string prefix=meters>0?NavigationPrompts.Number(meters).Replace("二百","两百",StringComparison.Ordinal)+"米处，":"";
        string limit=speedLimitKph is >0 and <=300?"，限速"+Number(speedLimitKph.Value):"";
        return prefix+"有测速照相"+limit+"。";
    }

    public static string Number(int value)
    {
        if (value is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(value));
        return JourneySpeechText.BelowTenThousand(value);
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

    public async ValueTask DisposeAsync()
    {
        ClearPlan();
        if (ownsQueue) await Queue.DisposeAsync();
    }
}
