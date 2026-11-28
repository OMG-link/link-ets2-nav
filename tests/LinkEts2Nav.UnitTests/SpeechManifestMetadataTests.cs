using System.Text.Json;
using System.Text.Json.Nodes;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
using LinkEts2Nav.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechManifestMetadataTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-pack-metadata-").FullName;
    private string Cache => Path.Combine(root, "cache");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Pack(string where, string text, byte sample, Action<JsonObject, JsonObject>? edit = null)
    {
        string directory = Path.Combine(root, where);
        string file = SpeechPack.FileName(text);
        string path = Path.Combine(directory, file);
        PcmAudio.Write(path, [sample, 0]);
        var entry = new JsonObject { ["Text"] = text, ["File"] = file, ["Sha256"] = SpeechPack.Hash(File.ReadAllBytes(path)) };
        var doc = new JsonObject { ["Schema"] = 1, ["Model"] = "model", ["Voice"] = "voice", ["ApiProfile"] = "profile",
            ["Entries"] = new JsonArray(entry) };
        edit?.Invoke(doc, entry);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), doc.ToJsonString());
        return directory;
    }

    internal static async Task<(int Status, string Message)> Import(string directory, SpeechPackGeneration generation, CancellationToken token)
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "application/json";
        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { directory }));
        context.Request.Body = body;
        context.Request.ContentLength = body.Length;
        var result = await PlayerSettingsServer.Import(context.Request, generation, token);
        var value = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        return (((IStatusCodeHttpResult)result).StatusCode ?? 200, value.GetProperty("message").GetString()!);
    }

    [Theory]
    [InlineData("Model")]
    [InlineData("Voice")]
    [InlineData("ApiProfile")]
    [InlineData("VoiceProfile")]
    [InlineData("Composed")]
    public async Task WrongOptionalTypeKeepsExactAudioButCannotAuthorizeComposition(string property)
    {
        const string text = "右转。";
        string directory = Pack("pack", text, 4, (doc, entry) =>
            (property is "VoiceProfile" or "Composed" ? entry : doc)[property] = 17);
        Pack("pack/Navigation", "然后", 6);
        var settings = new SpeechPackSettings(root);
        var generation = new SpeechPackGeneration(root, settings, () => null);
        Assert.Single(PreGeneratedSpeech.Load(directory));
        Assert.Equal(200, (await Import(directory, generation, Token)).Status);
        Assert.Single(settings.Pending!.Fixed);
        var worker = new SpeechPack(directory, Cache);
        Assert.Equal(new byte[] { 4,0 }, PcmAudio.Read(worker.Generate(text, _ => throw new Exception(), true).Path));
        const string combined = "然后，右转。";
        Assert.Equal(("fallback", false), worker.Generate(combined, t => { Assert.Equal(combined, t); return ("fallback", false); }));
    }

    [Theory]
    [InlineData("Schema")]
    [InlineData("Entries")]
    [InlineData("Text")]
    [InlineData("File")]
    [InlineData("Sha256")]
    [InlineData("WAV")]
    public async Task BadRequiredContentReturnsAFormatErrorAndPreservesTheCurrentSelection(string property)
    {
        string good = Pack("valid", "左转。", 1);
        string bad = Pack("bad", "右转。", 2, (doc, entry) =>
        {
            if (property != "WAV") (property is "Schema" or "Entries" ? doc : entry)[property] = new JsonObject();
            else
            {
                string file = Path.Combine(root, "bad", entry["File"]!.GetValue<string>());
                byte[] bytes = File.ReadAllBytes(file);
                bytes[24] = 0; // Wrong sample rate with a matching hash.
                File.WriteAllBytes(file, bytes);
                entry["Sha256"] = SpeechPack.Hash(bytes);
            }
        });
        var settings = new SpeechPackSettings(root);
        var generation = new SpeechPackGeneration(root, settings, () => null);
        var prior = await generation.SelectPackAsync(good, Token);
        var response = await Import(bad, generation, Token);
        Assert.Equal(400, response.Status); Assert.Contains("格式", response.Message);
        Assert.Same(prior, settings.Pending);
        Assert.Throws<InvalidDataException>(() => PreGeneratedSpeech.Load(bad));
        Assert.Equal(("fallback", false), new SpeechPack(bad, Cache).Generate("右转。", _ => ("fallback", false)));
    }

    [Fact]
    public void ACameraCarrierOrRegisteredNameWithInvalidMetadataCannotEnterAMixedRecording()
    {
        string camera = Pack("camera", "米处，有测速照相，限速", 3, (doc, _) => doc["ApiProfile"] = new JsonArray());
        const string warning = "一百米处，有测速照相，限速四十。";
        Assert.Equal(("fallback", false), new SpeechPack(camera, Cache).Generate(warning,
            t => { Assert.Equal(warning, t); return ("fallback", false); }));

        const string generic = "进入城区，请注意减速慢行。";
        string city = Pack("city", generic, 4);
        Pack("city/CityNames", "Húsavík", 9, (_, entry) => entry["VoiceProfile"] = new JsonObject());
        var worker = new SpeechPack(city, Cache);
        var response = worker.Generate("进入Húsavík城区，请注意减速慢行。", _ => throw new Exception());
        Assert.Equal(new byte[] { 4,0 }, PcmAudio.Read(response.Path));
    }

    [Fact]
    public void InvalidMetadataOnAnExactChineseCarrierDoesNotLeakThroughRegisteredForeignNames()
    {
        string city = Pack("city", "进入", 7, (_, entry) => entry["Composed"] = "unknown");
        Pack("city/CityNames", "Húsavík", 9, (doc, _) => doc["Voice"] = "foreign");
        var requests = new List<string>();
        var result = new SpeechPack(city, Cache).Generate("进入Húsavík城区，请注意减速慢行。", t =>
        {
            requests.Add(t);
            string path = Path.Combine(Cache, SpeechPack.FileName(t));
            PcmAudio.Write(path, [4,0]);
            return (path, false);
        });
        Assert.Equal(new[] { "进入", "城区，请注意减速慢行。" }, requests);
        Assert.Equal(new byte[] { 4,0, 9,0, 4,0 }, PcmAudio.Read(result.Path));
    }

    [Fact]
    public async Task AnExclusiveFileLockIsReportedSeparatelyFromAReservedGenerationDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        string pack = Pack("pack", "左转。", 1);
        using var lease = File.Open(Path.Combine(pack,"manifest.json"), FileMode.Open, FileAccess.Write, FileShare.None);
        var generation = new SpeechPackGeneration(root, new SpeechPackSettings(root), () => null);
        var response = await Import(pack, generation, Token);
        Assert.Equal(409, response.Status); Assert.Contains("占用", response.Message);
        Assert.DoesNotContain("正在写入", response.Message);
    }
    public void Dispose() => Directory.Delete(root, true);
}
