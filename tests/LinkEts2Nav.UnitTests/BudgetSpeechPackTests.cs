using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LinkEts2Nav.Speech;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinkEts2Nav.UnitTests;

[Collection("Local speech API")]
public sealed class BudgetSpeechPackTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-budget-").FullName;
    private readonly ConcurrentQueue<string> counted = new(), spoken = new();
    private sealed record Candidate(string Text, long PlayedCount, double PreparationMilliseconds = 0);
    private const string Template = """{"input":"{{text}}","model":"{{model}}","voice":"{{voice}}"}""";

    private async Task<WebApplication> Api(Func<string, long> price, Func<string, byte> sample)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        async Task<string> Input(HttpContext ctx)
        {
            Assert.Equal("fake-only", ctx.Request.Headers["X-Test"].ToString());
            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            Assert.Equal("model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("voice", body.RootElement.GetProperty("voice").GetString());
            return body.RootElement.GetProperty("input").GetString()!;
        }
        app.MapPost("/count", async ctx =>
        {
            string text = await Input(ctx);
            counted.Enqueue(text);
            await ctx.Response.WriteAsJsonAsync(new { tokens = price(text) });
        });
        app.MapPost("/speak", async ctx =>
        {
            string text = await Input(ctx);
            spoken.Enqueue(text);
            string path = Path.Combine(root, "response.wav");
            PcmAudio.Write(path, [sample(text), 0]);
            await ctx.Response.SendFileAsync(path);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private async Task<JsonElement> Run(WebApplication server, string source, string output, Candidate[] candidates, long budget)
    {
        string endpoint = server.Urls.Single();
        var job = new { Endpoint = endpoint + "/speak", Model = "model", Voice = "voice", ApiKey = "fake-only",
            RequestTemplate = Template, AuthHeader = "X-Test", AuthPrefix = "", Candidates = candidates,
            OutputDirectory = Path.Combine(root, output), CacheDirectory = Path.Combine(root, "cache"),
            SourcePack = Path.Combine(root, source), TokenBudget = budget, TokenCountEndpoint = endpoint + "/count" };
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false) };
        start.Environment["NO_PROXY"] = "127.0.0.1";
        start.ArgumentList.Add(typeof(SpeechPack).Assembly.Location);
        start.ArgumentList.Add("--pack-generation-worker");
        using var worker = Process.Start(start)!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var errors = worker.StandardError.ReadToEndAsync(stop.Token);
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(job));
            await worker.StandardInput.FlushAsync(stop.Token);
            JsonElement final = default;
            while (await worker.StandardOutput.ReadLineAsync(stop.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                final = doc.RootElement.Clone();
                Assert.InRange(final.GetProperty("SpentTokens").GetInt64(), 0, budget);
                if (final.GetProperty("Kind").GetString() is not "progress") break;
            }
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(stop.Token);
            Assert.DoesNotContain("fake-only", await errors);
            return final;
        }
        finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
    }

    private PackManifest ReadPack(string pack) => JsonSerializer.Deserialize<PackManifest>(
        File.ReadAllText(Path.Combine(root, pack, "manifest.json")))!;

    [Fact]
    public async Task SharesNaturalClausesAndKeepsWholeRecordingsAndImportedResourcesAcrossUpgrades()
    {
        string[] prefixes = ["五百米后，", "两百米后，", "一公里后，"];
        string[] tails = ["向左转。", "向右转。", "靠右行驶。"];
        const string hot = "红灯即将变绿。", original = "现有录音。";
        string[] clauses = prefixes.Concat(tails).ToArray();
        byte Sample(string t) => (byte)(Array.IndexOf(clauses, t) + 2);
        long Cost(string t) => t == hot ? 3 : clauses.Contains(t) ? 1 : 7;
        await using var api = await Api(Cost, Sample);
        var candidates = (from p in prefixes from t in tails select new Candidate(p + t, 10))
            .Prepend(new(hot, 40)).Append(new(original, 7)).ToArray();
        string source = Path.Combine(root, "original");
        await SpeechPackBuilder.BuildAsync(source, "import", "original", [original, "其他录音。"],
            (_, _) => Task.FromResult<byte[]>([91, 0]), cancellationToken: TestContext.Current.CancellationToken);
        string names = Directory.CreateDirectory(Path.Combine(source, "CityNames")).FullName;
        await File.WriteAllTextAsync(Path.Combine(names, "extra.txt"), "original language", TestContext.Current.CancellationToken);
        string journey = Directory.CreateDirectory(Path.Combine(source, "Journey")).FullName;
        File.WriteAllText(Path.Combine(journey, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(source, "context-provenance.json"), "{}");

        var first = await Run(api, "original", "first", candidates, 9);
        Assert.Equal("done", first.GetProperty("Kind").GetString());
        Assert.Equal(9, first.GetProperty("SpentTokens").GetInt64());
        Assert.Equal(2, first.GetProperty("WholeCount").GetInt32());
        Assert.Equal(9, first.GetProperty("ComposedCount").GetInt32());
        Assert.Equal(clauses.Append(hot).Order(), spoken.Order());
        Assert.Equal(counted.Count, counted.Distinct().Count());
        Assert.DoesNotContain(original, counted);
        var firstPack = ReadPack("first");
        Assert.Equal(12, firstPack.Entries.Length);
        foreach (string p in prefixes)
            foreach (string t in tails)
            {
                var entry = Assert.Single(firstPack.Entries, e => e.Text == p + t);
                Assert.True(entry.Composed);
                Assert.Equal(new byte[] { Sample(p), 0, Sample(t), 0 }, PcmAudio.Read(Path.Combine(root, "first", entry.File)));
            }

        string next = prefixes[0] + tails[0];
        var second = await Run(api, "first", "second", [new(next, 100), new(original, 10)], 7);
        Assert.Equal("done", second.GetProperty("Kind").GetString());
        Assert.Equal(2, second.GetProperty("WholeCount").GetInt32());
        Assert.Equal(7, second.GetProperty("SpentTokens").GetInt64());
        Assert.Equal(next, spoken.Last());
        Assert.Equal(8, spoken.Count);
        var after = ReadPack("second");
        Assert.Equal(12, after.Entries.Length);
        Assert.False(Assert.Single(after.Entries, e => e.Text == next).Composed);
        Assert.Equal("", Assert.Single(after.Entries, e => e.Text == original).VoiceProfile);
        Assert.Equal(new byte[] { 91, 0 }, PcmAudio.Read(Path.Combine(root, "second", SpeechPack.FileName(original))));
        Assert.True(File.Exists(Path.Combine(root, "second", "CityNames", "extra.txt")));
        Assert.True(File.Exists(Path.Combine(root, "second", "context-provenance.json")));
        Assert.True(File.Exists(Path.Combine(root, "second", "Journey", "manifest.json")));
        var rotated = await Run(api, "empty", "first", [new(hot, 40)], 1);
        Assert.Equal("done", rotated.GetProperty("Kind").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "first", "Journey")));
    }

    [Fact]
    public async Task PrefersTheMuchMoreFrequentlyHeardWholeOverAnExtraRareComposedPhrase()
    {
        const string prefix = "前方，", hot = "左转。", rare = "右转。";
        long Price(string t) => t switch { prefix + hot => 2, prefix + rare => 3, rare => 0, _ => 1 };
        await using var api = await Api(Price, _ => 2);
        var reply = await Run(api, "empty", "ranked", [new(prefix + hot, 100), new(prefix + rare, 1)], 2);
        Assert.Equal("done", reply.GetProperty("Kind").GetString());
        Assert.Equal(new[] { prefix + hot }, spoken);
        Assert.False(Assert.Single(ReadPack("ranked").Entries).Composed);
    }

    [Fact]
    public async Task RejectsAnInvalidCounterBeforeTheFirstSynthesisAndKeepsTheActivePackage()
    {
        await using var api = await Api(t => t == "bad" ? -1 : 1, _ => 2);
        var good = await Run(api, "empty", "old", [new("good", 1)], 10);
        Assert.Equal("done", good.GetProperty("Kind").GetString());
        var original = File.ReadAllBytes(Path.Combine(root, "old", "manifest.json"));
        var bad = await Run(api, "old", "failed", [new("new", 2), new("bad", 1)], 10);
        Assert.Equal("error", bad.GetProperty("Kind").GetString());
        Assert.Equal("token-count", bad.GetProperty("Code").GetString());
        Assert.Equal(0, bad.GetProperty("SpentTokens").GetInt64());
        Assert.Equal(new[] { "good" }, spoken);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, "old", "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "failed")));
    }

    [Fact]
    public async Task KeepsForeignPlaceNamesAndGroupedNumbersIntactAtNaturalBreaks()
    {
        const string name = "在 St. Louis 的 1,000 至 2，000 号出口，", turn = "向右转。";
        await using var api = await Api(_ => 1, _ => 1);
        var reply = await Run(api, "empty", "names", [new(name + turn, 2)], 1);
        Assert.Equal("done", reply.GetProperty("Kind").GetString());
        Assert.Equal(new[] { name + turn, name, turn }.Order(), counted.Order());
        Assert.Equal(new[] { name + turn }, spoken);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
