using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechPackFormatTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-pack-format-").FullName;
    private const string Text = "右转。";
    private static readonly byte[] Samples = [8, 0, 9, 0];

    private string MakePack(string relative, string text, string shape)
    {
        string directory = Path.Combine(root, relative);
        Directory.CreateDirectory(directory);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        void Chunk(string id, byte[] payload, bool pad = true)
        {
            writer.Write(Encoding.ASCII.GetBytes(id));
            writer.Write(payload.Length);
            writer.Write(payload);
            if (pad && payload.Length % 2 != 0) writer.Write((byte)0);
        }
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
        Chunk("JUNK", [1, 2, 3]);
        if (shape == "early-data") Chunk("data", Samples);
        // Legal extended PCM fmt chunk with a zero-length extension.
        byte[] fmt = [1,0,1,0,0xC0,0x5D,0,0,0x80,0xBB,0,0,2,0,16,0,0,0];
        if (shape == "wrong-rate") fmt[4] = 0;
        Chunk("fmt ", fmt);
        if (shape != "early-data") Chunk("data", Samples);
        if (shape == "duplicate-data") Chunk("data", Samples);
        if (shape == "partial-tail") writer.Write((byte)7);
        if (shape == "missing-pad") Chunk("JUNK", [3], false);
        var wav = stream.ToArray();
        if (shape != "wrong-riff-size") BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        string file = SpeechPack.FileName(text);
        File.WriteAllBytes(Path.Combine(directory, file), wav);
        // Legacy whole packs had no Model or Voice. A lowercase hex digest represents the same bytes.
        string hash = shape == "wrong-hash" ? new string('0', 64) : SpeechPack.Hash(wav);
        if (shape == "lowercase-hash") hash = hash.ToLowerInvariant();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            Schema = 1, Entries = new[] { new { Text = text, File = file, Sha256 = hash } }
        }));
        return directory;
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("lowercase-hash")]
    [InlineData("wrong-riff-size")]
    [InlineData("wrong-rate")]
    [InlineData("early-data")]
    [InlineData("duplicate-data")]
    [InlineData("partial-tail")]
    [InlineData("missing-pad")]
    [InlineData("wrong-hash")]
    public async Task StartupSelectionAndWorkerAgreeOnTheSameCheckedBytes(string shape)
    {
        string dir = MakePack("pack", Text, shape);
        var settings = new SpeechPackSettings(root);
        var worker = new SpeechPack(dir, Path.Combine(root, "cache"));
        var token = TestContext.Current.CancellationToken;
        if (shape is not ("valid" or "lowercase-hash"))
        {
            Assert.Throws<InvalidDataException>(() => PreGeneratedSpeech.Load(dir));
            await Assert.ThrowsAsync<InvalidDataException>(() => settings.SaveAsync(dir, token));
            Assert.Null(settings.Pending);
            Assert.Equal(("fallback", false), worker.Generate(Text, t => { Assert.Equal(Text, t); return ("fallback", false); }));
            return;
        }
        string source = Path.Combine(dir, SpeechPack.FileName(Text));
        Assert.Equal(source, PreGeneratedSpeech.Load(dir)[Text]);
        Assert.Equal(source, (await settings.SaveAsync(dir, token)).Fixed[Text]);
        var first = worker.Generate(Text, _ => throw new InvalidOperationException("Unexpected local synthesis."));
        Assert.False(first.CacheHit); Assert.Equal(Samples, PcmAudio.Read(first.Path));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(first.Path));
        Assert.True(worker.Generate(Text, _ => throw new InvalidOperationException()).CacheHit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupKeepsTheOtherDirectoryAndItsLocalFallback(bool badRoot)
    {
        string dir = MakePack("pack", Text, badRoot ? "wrong-riff-size" : "valid");
        string names = MakePack(Path.Combine("pack", "CityNames"), "Húsavík", badRoot ? "valid" : "wrong-riff-size");
        var messages = new List<string>();
        var settings = new SpeechPackSettings(root);
        await using var speech = new ReloadableSpeech(new SpeechOptions { PackDirectory = dir }, settings, messages.Add, _ => new Dummy());
        Assert.Equal(!badRoot, speech.Fixed.ContainsKey(Text));
        Assert.Equal(badRoot, speech.Names.Contains("Húsavík"));
        Assert.Contains(messages, m => m.Contains("未加载") && m.Contains(badRoot ? dir : names));
        Assert.Equal("local", await speech.SynthesizeAsync("未录制。", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => settings.SaveAsync(dir, TestContext.Current.CancellationToken));
        Assert.Null(settings.Pending);
    }

    private sealed class Dummy : ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text, CancellationToken token) => Task.FromResult("local");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() => Directory.Delete(root, true);
}
