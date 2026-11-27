using System.Net;
using System.Text.Json;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinkEts2Nav.UnitTests;

[CollectionDefinition("Local speech API", DisableParallelization = true)]
public sealed class LocalSpeechApiCollection;

[Collection("Local speech API")]
public sealed class PopularSpeechPackTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-popular-test-").FullName;
    private const string Secret = "test-only-local-credential";
    private readonly SpeechPackGeneration generation;
    private readonly SpeechPackSettings settings;
    private SpeechUsageSnapshot? liveUsage;
    private readonly string? priorNoProxy = Environment.GetEnvironmentVariable("no_proxy");
    private SpeechPackGenerationRequest request = new("", "my-model", "my-voice", Secret);
    private string Output => settings.Read().ResolvedDirectory;
    private string Usage => Path.Combine(root, "Cache", "LinkEts2Nav", "SpeechUsage", "summary.json");
    private string Manifest => Path.Combine(Output, "manifest.json");

    public PopularSpeechPackTests()
    {
        // Let the fake server observe the worker's socket, including disconnects. This testhost and
        // its child are isolated from other tests and no persistent system/user variables are changed.
        string? bypass = priorNoProxy ?? Environment.GetEnvironmentVariable("NO_PROXY");
        Environment.SetEnvironmentVariable("no_proxy", string.IsNullOrEmpty(bypass) ? "127.0.0.1,localhost" : bypass + ",127.0.0.1,localhost",
            EnvironmentVariableTarget.Process);
        settings = new(root);
        generation = new(root, settings, () => liveUsage, typeof(SpeechPack).Assembly.Location);
    }

    private async Task<SpeechPackGenerationStatus> Finished()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        while (generation.Status.Phase == SpeechPackGenerationPhase.Running) await Task.Delay(10, stop.Token);
        return generation.Status;
    }

    private async Task<SpeechPackGenerationStatus> Run(int top, SpeechPackGenerationPhase expected = SpeechPackGenerationPhase.Succeeded)
    {
        Assert.True(generation.TryStart(request with { Top = top }));
        var result = await Finished();
        Assert.True(result.Phase == expected, result.ToString());
        if (expected == SpeechPackGenerationPhase.Succeeded)
        {
            Assert.Equal(Output, result.OutputDirectory);
            Assert.Equal(result.Total, result.Completed);
        }
        return result;
    }

    private void SaveUsage(params (string Text, int Plays, double Preparation)[] entries)
    {
        var collector = new SpeechUsageCollector();
        foreach (var e in entries)
        {
            for (int i = 0; i < e.Plays; i++) collector.Played(e.Text, SpeechPlaybackOrigin.Prepared);
            collector.Prepared(e.Text, e.Preparation, true);
        }
        for (int i = 0; i < 20; i++) collector.Played("fallback tone only", SpeechPlaybackOrigin.Fallback);
        var snapshot = collector.Capture();
        Directory.CreateDirectory(Path.GetDirectoryName(Usage)!);
        File.WriteAllText(Usage, JsonSerializer.Serialize(snapshot with { Entries = snapshot.Entries.Reverse().ToArray() }));
    }

    private void SaveConfig(string endpoint, int speed = 1)
    {
        var body = JsonSerializer.Serialize(new { model = "{{model}}", voice = "{{voice}}", parameters = new { speed },
            input = new[] { new { text = "{{text}}" } } });
        request = new(endpoint, "my-model", "my-voice", Secret, RequestTemplate: body, AuthHeader: "X-Test-Key", AuthPrefix: "");
    }

    private static async Task<WebApplication> StartApi(RequestDelegate serve)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/speak", serve);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private byte[] Wave()
    {
        string path = Path.Combine(root, "test.wav");
        PcmAudio.Write(path, [2, 0, 3, 0]);
        return File.ReadAllBytes(path);
    }

    [Fact] public async Task UsesActualPlaybackRankingAndReusesOnlyTheSameConfiguredApiVoice()
    {
        const string first = "右转。", second = "含有换引号 \" 和 {{voice}} 的原文。";
        var received = new List<string>();
        byte[] wave = Wave();
        await using var api = await StartApi(async context =>
        {
            Assert.Equal(Secret, context.Request.Headers["X-Test-Key"].ToString());
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            var body = doc.RootElement;
            Assert.Equal("my-model", body.GetProperty("model").GetString());
            Assert.Equal("my-voice", body.GetProperty("voice").GetString());
            received.Add(body.GetProperty("input")[0].GetProperty("text").GetString()!);
            context.Response.ContentType = "audio/wav";
            await context.Response.Body.WriteAsync(wave);
        });
        string endpoint = api.Urls.Single() + "/speak";
        SaveConfig(endpoint);
        SaveUsage(("B", 3, 2), (second, 3, 20), ("A", 3, 2), (first, 7, 0), ("prepared only", 0, 999));
        await Run(3);
        Assert.Equal(new[] { first, second, "A" }, received);
        var pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Manifest))!;
        Assert.Equal(received, pack.Entries.Select(e => e.Text));
        Assert.Equal("my-model", pack.Model); Assert.Equal("my-voice", pack.Voice);
        var preloaded = PreGeneratedSpeech.Load(Output);
        Assert.Equal(3, preloaded.Count);
        foreach (string chosen in received) Assert.Equal(new byte[] { 2, 0, 3, 0 }, PcmAudio.Read(preloaded[chosen]));
        Assert.DoesNotContain(Secret, File.ReadAllText(Manifest));
        await Run(256); // Only four phrases have actually played, and three are already paid for.
        Assert.Equal(new[] { first, second, "A", "B" }, received);
        SaveConfig(endpoint, 2);
        await Run(1);
        Assert.Equal(new[] { first, second, "A", "B", first }, received);
        Assert.Equal(new[] { first }, PreGeneratedSpeech.Load(Output).Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedRequestOrInvalidWaveLeavesPreviousPackAndAccompanyingFilesIntact(bool statusError)
    {
        byte[] wave = Wave();
        var received = new List<string>();
        await using var api = await StartApi(async context =>
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            string text = doc.RootElement.GetProperty("input")[0].GetProperty("text").GetString()!;
            received.Add(text);
            if (text == "fail")
            {
                context.Response.StatusCode = statusError ? 429 : 200;
                await context.Response.WriteAsync(Secret);
            }
            else await context.Response.Body.WriteAsync(wave);
        });
        SaveConfig(api.Urls.Single() + "/speak");
        SaveUsage(("old", 1, 0));
        await Run(1);
        string initialOutput = Output;
        string names = Path.Combine(Output, "CityNames");
        await SpeechPackBuilder.BuildAsync(names, "original", "voice", ["Oslo"], (_, _) => Task.FromResult(new byte[] { 2, 0 }),
            cancellationToken: TestContext.Current.CancellationToken);
        var original = File.ReadAllBytes(Manifest);
        SaveUsage(("new", 9, 0), ("fail", 8, 0));
        var error = await Run(2, SpeechPackGenerationPhase.Failed);
        Assert.Contains(statusError ? "429" : "WAV", error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(initialOutput, Output);
        Assert.Equal(new[] { "old", "new", "fail" }, received);
        Assert.Equal(original, File.ReadAllBytes(Manifest));
        Assert.Contains("Oslo", PreGeneratedSpeech.Load(names, maximumEntries: 4096).Keys);
        Assert.Equal(new[] { "old" }, PreGeneratedSpeech.Load(Output).Keys);
        Assert.False(File.Exists(Path.Combine(Output, SpeechPack.FileName("new"))));
        SaveUsage(("later", 2, 0));
        await Run(1);
        Assert.Contains("Oslo", settings.Pending!.Names.Keys);
        Assert.NotEqual(initialOutput, Output);
    }

    [Fact] public async Task ValidatesTheUsageAndRuntimeLimitsBeforeAnyPaidRequest()
    {
        int calls = 0;
        await using var api = await StartApi(_ => { calls++; return Task.CompletedTask; });
        SaveConfig(api.Urls.Single() + "/speak");
        SaveUsage(("never played", 0, 999));
        Assert.Contains("播放", (await Run(2, SpeechPackGenerationPhase.Failed)).Message);
        Assert.Contains("256", (await Run(257, SpeechPackGenerationPhase.Failed)).Message);
        var tooLarge = Enumerable.Range(0, 64).Select(i => (new string('词', 1000) + i, 1, 0d)).ToArray();
        SaveUsage(tooLarge);
        await Run(64, SpeechPackGenerationPhase.Failed);
        File.WriteAllText(Usage, """{"Schema":1,"MetricVersion":2,"Entries":[]}""");
        await Run(1, SpeechPackGenerationPhase.Failed);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Output));
    }

    [Fact] public async Task CancellingThePrivateWorkerPreservesTheActivePackAndResumesVerifiedClipsWithoutRebilling()
    {
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] wave = Wave();
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool hang = true;
        await using var api = await StartApi(async context =>
        {
            Assert.Equal("Bearer " + Secret, context.Request.Headers.Authorization.ToString());
            using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            string text = doc.RootElement.GetProperty("input").GetString()!;
            received.Enqueue(text);
            if (text == "two" && Volatile.Read(ref hang))
            {
                context.Response.ContentType = "audio/wav";
                context.Response.ContentLength = 16 * 1024 * 1024;
                try
                {
                    // Keep an actual response in flight so Kestrel observes a disconnected reader
                    // on Windows as well as a reset before the provider sends response headers.
                    while (true)
                    {
                        await context.Response.Body.WriteAsync(wave, context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        inFlight.TrySetResult();
                        await Task.Delay(25, context.RequestAborted);
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or IOException) { aborted.TrySetResult(); }
                return;
            }
            await context.Response.Body.WriteAsync(wave);
        });
        request = new(api.Urls.Single() + "/speak", "model", "voice", Secret, 3);
        SaveUsage(("old disk text", 5, 0));
        await Run(1);
        string old = Output;
        var collector = new SpeechUsageCollector();
        foreach (string text in new[] { "one", "two", "one", "three", "two", "one" }) collector.Played(text, SpeechPlaybackOrigin.Prepared);
        liveUsage = collector.Capture();
        Assert.True(generation.TryStart(request));
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(generation.TryStart(request));
        using var progressTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        progressTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (generation.Status.Completed == 0) await Task.Delay(10, progressTimeout.Token);
        Assert.Equal(1, generation.Status.Completed);
        string reserved = Path.Combine(root, "SpeechPacks", "LinkEts2Nav", "Popular-2");
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generation.SelectPackAsync(reserved, TestContext.Current.CancellationToken));
        Assert.Contains("正在生成", busy.Message);
        Assert.Equal(old, (await generation.SelectPackAsync(old, TestContext.Current.CancellationToken)).Directory);
        Assert.True(generation.TryCancel());
        Assert.Equal(SpeechPackGenerationPhase.Cancelled, (await Finished()).Phase);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(old, Output);
        Assert.False(generation.TryCancel());
        Volatile.Write(ref hang, false);
        await Run(3);
        Assert.Equal(new[] { "old disk text", "one", "two", "two", "three" }, received);
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, File.ReadAllText(file));
        Assert.DoesNotContain(Secret, request.ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("no_proxy", priorNoProxy, EnvironmentVariableTarget.Process);
        Directory.Delete(root, recursive: true);
    }
}
