using System.Text.Json;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class NameSpeechJobTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nav-names-" + Guid.NewGuid().ToString("N"));
    private string List(string file, params string[] texts)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, file);
        File.WriteAllLines(path, texts);
        return path;
    }
    private string Jobs => Path.Combine(root, "missing.jobs.json");

    [Fact]
    public void DefaultCatalogPreparesNarrowWarningsAndSharedNameFragments()
    {
        Assert.Contains("五百米后，进入窄路，请减速观察。", SpeechTextCatalog.DefaultTexts);
        Assert.Contains("两百米后，进入窄路，请减速观察。", SpeechTextCatalog.DefaultTexts);
        Assert.Contains("城区，请注意减速慢行。", SpeechTextCatalog.DefaultTexts);
        Assert.Contains("码头，乘坐前往", SpeechTextCatalog.DefaultTexts);
        Assert.Contains("货场附近，本次导航结束。", SpeechTextCatalog.DefaultTexts);
        Assert.DoesNotContain("经过出口。", SpeechTextCatalog.DefaultTexts);
    }

    [Fact]
    public async Task MergesNormalizedNamesAndKeepsDistinctExactAliasesAndSources()
    {
        await NameSpeechJobs.RunAsync(["--prepare-name-jobs", "--texts", List("cities.txt", " Cafe\u0301 ", "Vík"),
            "--texts", List("signs.txt", "Café", "VÍK"), "--output", Jobs]);
        var jobs = JsonSerializer.Deserialize<SpeechJobs>(File.ReadAllText(Jobs))!;
        Assert.Equal(3, jobs.Items.Length);
        Assert.Contains(jobs.Items, j => j.Text == "Café");
        using var coverage = JsonDocument.Parse(File.ReadAllText(Jobs + ".coverage.json"));
        Assert.Equal(2, coverage.RootElement.EnumerateArray().Single(e => e.GetProperty("Text").GetString() == "Café")
            .GetProperty("Sources").GetArrayLength());
    }

    [Fact]
    public async Task OnlyValidatedClipsAreRemovedFromGenerationJobs()
    {
        string list = List("names.txt", "Oslo", "Bergen", "Dover");
        string pack = Path.Combine(root, "pack"); Directory.CreateDirectory(pack);
        var ready = Entry("Oslo"); var corrupt = Entry("Bergen");
        File.WriteAllText(Path.Combine(pack, "manifest.json"), JsonSerializer.Serialize(new PackManifest(1, "test", "test", [ready, corrupt])));
        File.WriteAllText(Path.Combine(pack, corrupt.File), "broken");
        await NameSpeechJobs.RunAsync(["--prepare-name-jobs", "--texts", list, "--pack", pack, "--output", Jobs]);
        var jobs = JsonSerializer.Deserialize<SpeechJobs>(File.ReadAllText(Jobs))!;
        Assert.Equal(new[] { "Bergen", "Dover" }, jobs.Items.Select(j => j.Text));
        PackEntry Entry(string name)
        {
            string file = SpeechPack.FileName(name), path = Path.Combine(pack, file);
            PcmAudio.Write(path, [1, 0, 2, 0]);
            return new(name, file, SpeechPack.Hash(File.ReadAllBytes(path)));
        }
    }

    [Fact]
    public async Task CompletedCoverageProducesEmptyJobsWithoutRequestingAudio()
    {
        string list = List("names.txt", "Oslo"); string pack = Path.Combine(root, "pack");
        await SpeechPackBuilder.BuildAsync(pack, "test", "test", ["Oslo"], (_, _) => Task.FromResult(new byte[] { 1, 0 }),
            cancellationToken: TestContext.Current.CancellationToken);
        await NameSpeechJobs.RunAsync(["--prepare-name-jobs", "--texts", list, "--pack", pack, "--output", Jobs]);
        Assert.Empty(JsonSerializer.Deserialize<SpeechJobs>(File.ReadAllText(Jobs))!.Items);
    }

    [Fact]
    public async Task RejectsOversizedCombinedPackAndInputOverwrite()
    {
        string list = List("names.txt", Enumerable.Range(0, 4097).Select(i => "Name" + i).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => NameSpeechJobs.RunAsync(["--prepare-name-jobs", "--texts", list, "--output", Jobs]));
        list = List("names.txt", "Oslo");
        await Assert.ThrowsAsync<ArgumentException>(() => NameSpeechJobs.RunAsync(["--prepare-name-jobs", "--texts", list, "--output", list]));
        Assert.Equal("Oslo", File.ReadAllText(list).Trim());
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
