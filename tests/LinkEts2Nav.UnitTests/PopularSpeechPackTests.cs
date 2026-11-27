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

    private async Task<SpeechPackGenerationStatus> Run(long budget, SpeechPackGenerationPhase expected = SpeechPackGenerationPhase.Succeeded)
    {
        Assert.True(generation.TryStart(request with { TokenBudget = budget }));
        var result = await Finished();
        Assert.True(result.Phase == expected, result.ToString());
        Assert.Equal(budget, result.Budget);
        if (expected == SpeechPackGenerationPhase.Succeeded)
        {
            Assert.Equal(Output, result.OutputDirectory);
            Assert.Equal(result.Total, result.Completed);
            Assert.Equal(result.Completed, result.WholeCount + result.ComposedCount);
            Assert.InRange(result.SpentTokens, 0L, budget);
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
        request = new(endpoint, "my-model", "my-voice", Secret, RequestTemplate: body, AuthHeader: "X-Test-Key", AuthPrefix: "",
            TokenCountEndpoint: new Uri(new Uri(endpoint), "/count").AbsoluteUri);
    }

    private static async Task<WebApplication> StartApi(RequestDelegate serve)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/speak", serve);
        app.MapPost("/count", () => Results.Json(new { tokens = 1L }));
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
        Assert.Equal(3, (await Run(3)).SpentTokens);
        Assert.Equal(new[] { first, second, "A" }, received);
        var pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Manifest))!;
        Assert.Equal(received.Order(), pack.Entries.Select(e => e.Text).Order());
        Assert.Equal("my-model", pack.Model); Assert.Equal("my-voice", pack.Voice);
        var preloaded = PreGeneratedSpeech.Load(Output);
        Assert.Equal(3, preloaded.Count);
        foreach (string chosen in received) Assert.Equal(new byte[] { 2, 0, 3, 0 }, PcmAudio.Read(preloaded[chosen]));
        Assert.DoesNotContain(Secret, File.ReadAllText(Manifest));
        var expanded = await Run(long.MaxValue); // Only four phrases have actually played, and three are already paid for.
        Assert.Equal(1, expanded.SpentTokens);
        Assert.Equal(4, expanded.WholeCount);
        Assert.Equal(new[] { first, second, "A", "B" }, received);
        SaveConfig(endpoint, 2);
        await Run(1);
        Assert.Equal(new[] { first, second, "A", "B", first }, received);
        Assert.Contains(first, PreGeneratedSpeech.Load(Output).Keys);
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
        Assert.Equal(2, error.SpentTokens);
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

    [Fact] public async Task ValidatesBudgetCounterOriginAndIndividualUsageRecordsBeforeSynthesis()
    {
        int calls = 0;
        await using var api = await StartApi(_ => { calls++; return Task.CompletedTask; });
        SaveConfig(api.Urls.Single() + "/speak");
        SaveUsage(("never played", 0, 999));
        Assert.Contains("播放", (await Run(2, SpeechPackGenerationPhase.Failed)).Message);
        Assert.Contains("预算", (await Run(0, SpeechPackGenerationPhase.Failed)).Message);
        SaveUsage((new string('词', 2049), 1, 0));
        await Run(33000, SpeechPackGenerationPhase.Failed);
        request = request with { TokenCountEndpoint = "https://example.invalid/count" };
        SaveUsage(("spoken", 1, 0));
        Assert.Contains("主机", (await Run(50, SpeechPackGenerationPhase.Failed)).Message);
        request = request with { TokenCountEndpoint = null };
        File.WriteAllText(Usage, """{"Schema":1,"MetricVersion":2,"Entries":[]}""");
        await Run(1, SpeechPackGenerationPhase.Failed);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Output));
    }

    [Fact] public async Task MoreThan256PlayedPhrasesCanAllBeCoveredByOneNewInput()
    {
        byte[] wave = Wave();
        string source = Directory.CreateDirectory(Path.Combine(root, "recorded")).FullName;
        string[] recorded = Enumerable.Range(0, 257).Select(i => "recorded " + i).ToArray();
        var entries = new List<PackEntry>();
        foreach (string text in recorded)
        {
            string file = SpeechPack.FileName(text);
            File.WriteAllBytes(Path.Combine(source, file), wave);
            entries.Add(new(text, file, SpeechPack.Hash(wave)));
        }
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            JsonSerializer.Serialize(new PackManifest(1, "recorded", "voice", entries.ToArray())), TestContext.Current.CancellationToken);
        await settings.SaveAsync(source, TestContext.Current.CancellationToken);
        var requested = new List<string>();
        await using var api = await StartApi(async context =>
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            requested.Add(json.RootElement.GetProperty("input")[0].GetProperty("text").GetString()!);
            await context.Response.Body.WriteAsync(wave);
        });
        SaveConfig(api.Urls.Single() + "/speak");
        SaveUsage(recorded.Select(text => (text, 2, 0d)).Append(("last uncached", 1, 0d)).ToArray());
        var result = await Run(1);
        Assert.Equal(258, result.WholeCount);
        Assert.Equal(1, result.SpentTokens);
        Assert.Equal(new[] { "last uncached" }, requested);
        Assert.Equal(258, PreGeneratedSpeech.Load(Output).Count);
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
        request = new(api.Urls.Single() + "/speak", "model", "voice", Secret, 3, TokenCountEndpoint: api.Urls.Single() + "/count");
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
        await generation.CancelAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(SpeechPackGenerationPhase.Cancelled, (await Finished()).Phase);
        Assert.Equal(2, generation.Status.SpentTokens);
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
