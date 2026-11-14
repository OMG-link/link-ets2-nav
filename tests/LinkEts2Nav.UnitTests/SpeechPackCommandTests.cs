using System.Text.Json;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechPackCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nav-pack-cli-" + Guid.NewGuid().ToString("N"));
    private string Jobs => Path.Combine(root, "jobs.json");
    private string Input => Path.Combine(root, "input");
    private string Output => Path.Combine(root, "pack");

    private void Save(params SpeechJob[] items)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Jobs, JsonSerializer.Serialize(new SpeechJobs(1, "fixed", items)));
    }

    private Task<int> Import() => SpeechPackCommands.RunAsync(["--import-pack", "--jobs", Jobs,
        "--input", Input, "--output", Output, "--model", "my-tts", "--voice", "my-voice"]);

    [Fact]
    public async Task DefaultJobsExportAllDefaultTextsWithContentDerivedFilenames()
    {
        await SpeechPackCommands.RunAsync(["--export-jobs", "--output", Jobs]);
        var jobs = JsonSerializer.Deserialize<SpeechJobs>(File.ReadAllText(Jobs))!;
        Assert.Equal("fixed", jobs.Kind);
        Assert.Equal(237, jobs.Items.Length);
        Assert.Equal(SpeechPack.DefaultTexts(), jobs.Items.Select(j => j.Text));
        Assert.All(jobs.Items, j => Assert.Equal(SpeechPack.FileName(j.Text), j.File));
        Assert.Contains(jobs.Items, j => j.Text == SpeechPack.UncalibratedText);
        Assert.Contains(jobs.Items, j => j.Text == "到达目的地附近，导航结束。");
    }

    [Fact]
    public async Task ArbitraryProviderAudioImportsAndPlaysThroughExistingPackReader()
    {
        const string text = "五百米后，左转。";
        Save(new SpeechJob(text, "nested/custom.wav"));
        PcmAudio.Write(Path.Combine(Input, "nested/custom.wav"), [1, 0, 2, 0]);
        File.WriteAllText(Path.Combine(Input, "extra.mp3"), "ignored");
        await Import();
        var manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Path.Combine(Output, "manifest.json")))!;
        Assert.Equal("my-tts", manifest.Model); Assert.Equal("my-voice", manifest.Voice);
        Assert.Single(manifest.Entries);
        string file = manifest.Entries[0].File;
        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(Output, file));
        await Import();
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Output, file)));
        var result = new SpeechPack(Output, Path.Combine(root, "cache")).Generate(text, _ => throw new Exception("Must use imported pack."));
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, PcmAudio.Read(result.Path));
    }

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("missing.wav")]
    public async Task MissingOrEscapingInputsFailBeforeChangingOutput(string file)
    {
        Save(new SpeechJob("左转。", file));
        await Assert.ThrowsAnyAsync<Exception>(Import);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task DuplicateJobsAndInvalidAudioFailWithoutCreatingPack()
    {
        Save(new SpeechJob("左转。", "one.wav"), new SpeechJob("左转。", "two.wav"));
        await Assert.ThrowsAsync<InvalidDataException>(Import);
        Save(new SpeechJob("左转。", "invalid.wav"));
        Directory.CreateDirectory(Input); File.WriteAllText(Path.Combine(Input, "invalid.wav"), "not WAV");
        await Assert.ThrowsAsync<InvalidDataException>(Import);
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task CityNameJobsKeepOriginalUnicodeAndImportIntoCitySubpack()
    {
        Directory.CreateDirectory(root); string texts = Path.Combine(root, "names.txt");
        File.WriteAllText(texts, "Patreksfjörður\nÞórshöfn\n");
        await SpeechPackCommands.RunAsync(["--export-jobs", "--output", Jobs, "--texts", texts, "--kind", "city-names"]);
        var jobs = JsonSerializer.Deserialize<SpeechJobs>(File.ReadAllText(Jobs))!;
        foreach (var job in jobs.Items) PcmAudio.Write(Path.Combine(Input, job.File), [3, 0]);
        await SpeechPackCommands.RunAsync(["--import-pack", "--jobs", Jobs, "--input", Input, "--output", Path.Combine(Output, "CityNames")]);
        var result = new SpeechPack(Output, Path.Combine(root, "cache")).Generate("五百米后，进入Patreksfjörður城区，请注意减速慢行。", t =>
        {
            Assert.DoesNotContain("Patreksfjörður", t);
            string path = Path.Combine(root, SpeechPack.FileName(t)); PcmAudio.Write(path, [9, 0]); return (path, false);
        });
        Assert.Equal(new byte[] { 9, 0, 3, 0, 9, 0 }, PcmAudio.Read(result.Path));
    }

    [Fact]
    public void TextCatalogRejectsNormalizedDuplicatesAndEmptyLists()
    {
        Assert.Throws<InvalidDataException>(() => SpeechTextCatalog.Read(new StringReader("\n\n")));
        Assert.Throws<InvalidDataException>(() => SpeechTextCatalog.Read(new StringReader("é\ne\u0301\n")));
    }

    [Fact]
    public async Task ImportRejectsOverlappingInputAndOutputBeforeWriting()
    {
        Save(new SpeechJob("左转。", "clip.wav"));
        PcmAudio.Write(Path.Combine(Input, "clip.wav"), [1, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SpeechPackCommands.RunAsync([
            "--import-pack", "--jobs", Jobs, "--input", Input, "--output", Path.Combine(Input, "nested-pack")]));
        Assert.False(Directory.Exists(Path.Combine(Input, "nested-pack")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
