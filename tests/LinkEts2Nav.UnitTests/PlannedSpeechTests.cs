using System.Threading.Channels;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class PlannedSpeechTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LinkNavSpeech-" + Guid.NewGuid().ToString("N"));
    public PlannedSpeechTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private string File(string name) { string path = Path.Combine(directory, name + ".wav"); System.IO.File.WriteAllBytes(path, [1]); return path; }
    private static WarningSnapshot Snapshot(bool active = true) => new("session", "route", true, active, Now, []);
    private static PlannedCameraWarning Plan(double stage, double distance, string camera = "camera") =>
        new(camera, stage, distance, WarningDistanceKind.Road, Math.Max(0, distance - stage));
    private static CameraWarning Warning(double stage, double distance) =>
        new("camera", distance, WarningDistanceKind.Road) { StageDistanceMeters = stage };

    private sealed class Fallback : IWarningAnnouncer
    {
        public List<CameraWarning> Warnings { get; } = [];
        public void Announce(CameraWarning warning) => Warnings.Add(warning);
    }
    private sealed record Request(string Text, TaskCompletionSource<string> Result);
    private sealed class Synthesizer : ISpeechSynthesizer
    {
        public Channel<Request> Requests { get; } = Channel.CreateUnbounded<Request>();
        public async Task<string> SynthesizeAsync(string text, CancellationToken token)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Requests.Writer.WriteAsync(new(text, result), token);
            return await result.Task.WaitAsync(token);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<Request> Next() => Requests.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UncalibratedNoticePlaysExactPreGeneratedSentenceEvenInside200Meters()
    {
        string path = File("uncalibrated");
        var played = new List<string>();
        var fallback = new Fallback();
        await using var speech = new PlannedSpeechAnnouncer(new Synthesizer(), fallback, played.Add,
            preGenerated: new Dictionary<string, string> { [PlannedSpeechAnnouncer.UncalibratedText] = path });
        speech.Announce(new("unknown", 150, WarningDistanceKind.Euclidean)
            { Kind = CameraWarningKind.UncalibratedProximity });
        Assert.Equal(path, Assert.Single(played));
        Assert.Empty(fallback.Warnings);
    }
    [Fact]
    public async Task PlansStartImmediatelyInTriggerOrderAndShareWholeSentenceCache()
    {
        var synth = new Synthesizer();
        var ready = Channel.CreateUnbounded<string>();
        var played = new List<string>();
        var fallback = new Fallback();
        await using var speech = new PlannedSpeechAnnouncer(synth, fallback, played.Add,
            message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.Writer.TryWrite(message); });
        speech.UpdatePlan(Snapshot(), [Plan(200, 900), Plan(500, 900), Plan(500, 1000, "other")], Now);
        var first = await synth.Next();
        Assert.Equal("五百米处，有测速照相。", first.Text);
        string firstFile = File("500");
        first.Result.SetResult(firstFile);
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = await synth.Next();
        Assert.Equal("两百米处，有测速照相。", second.Text);
        second.Result.SetResult(File("200"));
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        speech.Announce(Warning(500, 499));
        speech.Announce(Warning(200, 199));
        Assert.Equal(2, played.Count);
        Assert.Equal(firstFile, played[0]);
        Assert.Empty(fallback.Warnings);
        Assert.False(synth.Requests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task UnreadyTriggerFallsBackImmediatelyAndCompletionNeverPlaysLate()
    {
        var synth = new Synthesizer();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<string>();
        var fallback = new Fallback();
        await using var speech = new PlannedSpeechAnnouncer(synth, fallback, played.Add,
            message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.TrySetResult(); });
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        var request = await synth.Next();
        speech.Announce(Warning(500, 499));
        Assert.Single(fallback.Warnings);
        speech.ClearPlan();
        request.Result.SetResult(File("finished-after-trigger"));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(played);
        Assert.Single(fallback.Warnings);
    }

    [Fact]
    public async Task RouteChangesReorderUnstartedJobsAndPauseClearsThem()
    {
        var synth = new Synthesizer();
        var ready = Channel.CreateUnbounded<bool>();
        await using var speech = new PlannedSpeechAnnouncer(synth, new Fallback(), _ => { },
            message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.Writer.TryWrite(true); });
        speech.UpdatePlan(Snapshot(), [Plan(500, 300), Plan(200, 300), Plan(500, 1200, "other")], Now);
        var generic = await synth.Next();
        Assert.Equal("前方有测速照相。", generic.Text);
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        generic.Result.SetResult(File("generic"));
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var next = await synth.Next();
        Assert.Equal("五百米处，有测速照相。", next.Text);
        speech.UpdatePlan(Snapshot(active: false), [Plan(200, 600)], Now);
        next.Result.SetResult(File("500"));
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(synth.Requests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StopCancelsNativeRequestWithoutPlayingOrFallingBack()
    {
        var synth = new Synthesizer();
        var fallback = new Fallback();
        var played = new List<string>();
        var speech = new PlannedSpeechAnnouncer(synth, fallback, played.Add);
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        await synth.Next();
        await speech.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(played);
        Assert.Empty(fallback.Warnings);
    }

    [Fact]
    public async Task FailedSynthesisDoesNotRetryOnEveryTelemetryTick()
    {
        var synth = new Synthesizer();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = new Fallback();
        await using var speech = new PlannedSpeechAnnouncer(synth, fallback, _ => { },
            message => { if (message.StartsWith("TTS 合成失败")) failed.TrySetResult(); });
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        (await synth.Next()).Result.SetException(new IOException("missing model"));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        for (int i = 0; i < 20; i++) speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        speech.Announce(Warning(500, 499));
        Assert.Single(fallback.Warnings);
        Assert.False(synth.Requests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DeletedReadyFileIsRegeneratedEvenWhenThePlanIsUnchanged()
    {
        var synth = new Synthesizer();
        var ready = Channel.CreateUnbounded<bool>();
        var played = new List<string>();
        await using var speech = new PlannedSpeechAnnouncer(synth, new Fallback(), played.Add,
            message => { if (message.StartsWith("TTS 文件缓存已就绪")) ready.Writer.TryWrite(true); });
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        string old = File("old");
        (await synth.Next()).Result.SetResult(old);
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        System.IO.File.Delete(old);
        speech.UpdatePlan(Snapshot(), [Plan(500, 800)], Now);
        string replacement = File("regenerated");
        (await synth.Next()).Result.SetResult(replacement);
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        speech.Announce(Warning(500, 499));
        Assert.Equal(replacement, Assert.Single(played));
    }

    [Theory]
    [InlineData(500, 499, "五百米处，有测速照相。")]
    [InlineData(200, 199, "两百米处，有测速照相。")]
    [InlineData(500, 300, "前方有测速照相。")]
    [InlineData(200, 60, "两百米处，有测速照相。")]
    [InlineData(500, 150, "前方有测速照相。")]
    public void LateRecognitionDoesNotSpeakAnIncorrectFixedDistance(double stage, double actual, string expected)
        => Assert.Equal(expected, PlannedSpeechAnnouncer.Text(stage, actual));

    [Fact]
    public void SpeechSettingsResolvePathsFromHostAndRemainOptIn()
    {
        Assert.False(SpeechOptions.Load(directory).Enabled);
        Directory.CreateDirectory(Path.Combine(directory, "Settings"));
        System.IO.File.WriteAllText(Path.Combine(directory, "Settings", "LinkEts2Nav.Speech.json"),
            """{"enabled":true,"modelDirectory":"Models/kokoro","voice":"zf_067","threads":2}""");
        var options = SpeechOptions.Load(directory);
        Assert.Equal(Path.Combine(directory, "Models", "kokoro"), options.ModelDirectory);
        Assert.Equal(Path.Combine(directory, "Tools", "LinkEts2Nav.Speech", "LinkEts2Nav.Speech.dll"), options.WorkerPath);
        Assert.StartsWith(directory, options.CacheDirectory);
    }
}
