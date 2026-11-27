using System.Net;
using System.Text.Json;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinkEts2Nav.UnitTests;

public sealed class PopularSpeechPackTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-popular-test-").FullName;
    private readonly string variable = "NAV_TEST_TTS_" + Guid.NewGuid().ToString("N");
    private const string Secret = "test-only-local-credential";
    private string Output => Path.Combine(root, "pack");
    private string Usage => Path.Combine(root, "summary.json");
    private string Config => Path.Combine(root, "api.json");
    private string Manifest => Path.Combine(Output, "manifest.json");

    private Task<int> Run(int top) => SpeechPackCommands.RunAsync(["--generate-popular-pack", "--usage", Usage,
        "--top", top.ToString(System.Globalization.CultureInfo.InvariantCulture), "--api-config", Config, "--output", Output]);

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
        File.WriteAllText(Usage, JsonSerializer.Serialize(snapshot with { Entries = snapshot.Entries.Reverse().ToArray() }));
    }

    private void SaveConfig(string endpoint, int speed = 1)
    {
        Environment.SetEnvironmentVariable(variable, Secret);
        File.WriteAllText(Config, JsonSerializer.Serialize(new { endpoint, model = "my-model", voice = "my-voice",
            auth = new { header = "X-Test-Key", environmentVariable = variable, prefix = "" },
            request = new { model = "{{model}}", voice = "{{voice}}", parameters = new { speed },
                input = new[] { new { text = "{{text}}" } } } }));
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
        Assert.Equal(0, await Run(3));
        Assert.Equal(new[] { first, second, "A" }, received);
        var pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Manifest))!;
        Assert.Equal(received, pack.Entries.Select(e => e.Text));
        Assert.Equal("my-model", pack.Model); Assert.Equal("my-voice", pack.Voice);
        var preloaded = PreGeneratedSpeech.Load(Output);
        Assert.Equal(3, preloaded.Count);
        foreach (string chosen in received) Assert.Equal(new byte[] { 2, 0, 3, 0 }, PcmAudio.Read(preloaded[chosen]));
        Assert.DoesNotContain(Secret, File.ReadAllText(Manifest));
        Assert.Equal(0, await Run(256)); // Only four phrases have actually played, and three are already paid for.
        Assert.Equal(new[] { first, second, "A", "B" }, received);
        SaveConfig(endpoint, 2);
        Assert.Equal(0, await Run(1));
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
        string companion = Path.Combine(Directory.CreateDirectory(Path.Combine(Output, "CityNames")).FullName, "keep.txt");
        File.WriteAllText(companion, "unchanged");
        var original = File.ReadAllBytes(Manifest);
        SaveUsage(("new", 9, 0), ("fail", 8, 0));
        var error = await Record.ExceptionAsync(() => Run(2));
        if (statusError) { Assert.IsType<HttpRequestException>(error); Assert.Contains("429", error.Message); }
        else Assert.IsType<InvalidDataException>(error);
        Assert.DoesNotContain(Secret, error!.ToString());
        Assert.Equal(new[] { "old", "new", "fail" }, received);
        Assert.Equal(original, File.ReadAllBytes(Manifest));
        Assert.Equal("unchanged", File.ReadAllText(companion));
        Assert.Equal(new[] { "old" }, PreGeneratedSpeech.Load(Output).Keys);
        Assert.False(File.Exists(Path.Combine(Output, SpeechPack.FileName("new"))));
        SaveUsage(("later", 2, 0));
        await Run(1);
        Assert.Equal("unchanged", File.ReadAllText(companion));
    }

    [Fact] public async Task ValidatesTheUsageAndRuntimeLimitsBeforeAnyPaidRequest()
    {
        int calls = 0;
        await using var api = await StartApi(_ => { calls++; return Task.CompletedTask; });
        SaveConfig(api.Urls.Single() + "/speak");
        SaveUsage(("never played", 0, 999));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(2));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(257));
        var tooLarge = Enumerable.Range(0, 64).Select(i => (new string('词', 1000) + i, 1, 0d)).ToArray();
        SaveUsage(tooLarge);
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(64));
        File.WriteAllText(Usage, """{"Schema":1,"MetricVersion":2,"Entries":[]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(1));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Output));
    }

    public void Dispose() { Environment.SetEnvironmentVariable(variable, null); Directory.Delete(root, recursive: true); }
}
