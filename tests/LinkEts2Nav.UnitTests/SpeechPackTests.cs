using System.Text.Json;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechPackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nav-pack-" + Guid.NewGuid().ToString("N"));
    private string Pack => Path.Combine(root, "pack");
    private string Cache => Path.Combine(root, "cache");
    private readonly List<string> localRequests = [];

    private PackEntry Entry(string text, byte[] samples)
    {
        string name = SpeechPack.FileName(text), path = Path.Combine(Pack, name);
        PcmAudio.Write(path, samples);
        return new(text, name, SpeechPack.Hash(File.ReadAllBytes(path)));
    }
    private SpeechPack Create(params PackEntry[] entries)
    {
        Directory.CreateDirectory(Pack);
        File.WriteAllText(Path.Combine(Pack, "manifest.json"),
            JsonSerializer.Serialize(new PackManifest(1, "test-model", "test-voice", entries)));
        return new(Pack, Cache);
    }
    private (string Path, bool CacheHit) Local(string text)
    {
        localRequests.Add(text);
        string path = Path.Combine(Cache, SpeechPack.FileName(text));
        if (File.Exists(path)) return (path, true);
        PcmAudio.Write(path, text == "五百" ? [1, 0, 2, 0] : [9, 0]);
        return (path, false);
    }

    [Fact]
    public void CommonSentenceUsesCloudFileWithoutLocalInferenceAndRepairsCache()
    {
        const string text = "五百米处，有测速照相，限速九十公里。";
        var pack = Create(Entry(text, [10, 0, 11, 0]));
        var first = pack.Generate(text, Local);
        Assert.Empty(localRequests);
        Assert.StartsWith(Cache, first.Path);
        Assert.Equal(new byte[] { 10, 0, 11, 0 }, PcmAudio.Read(first.Path));
        Assert.True(pack.Generate(text, Local).CacheHit);
        File.WriteAllText(first.Path, "broken");
        Assert.False(pack.Generate(text, Local).CacheHit);
        Assert.Equal(new byte[] { 10, 0, 11, 0 }, PcmAudio.Read(first.Path));
    }

    [Fact]
    public void SpecialLimitJoinsOnlyLocalNumbersInCorrectOrderAndReusesCache()
    {
        var pack = Create(Entry("米处，有测速照相，限速", [3, 0, 4, 0]), Entry("公里。", [5, 0]));
        const string text = "五百米处，有测速照相，限速六十五公里。";
        var first = pack.Generate(text, Local);
        Assert.Equal(new[] { "五百", "六十五" }, localRequests);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0, 9, 0, 5, 0 }, PcmAudio.Read(first.Path));
        var second = pack.Generate(text, Local);
        Assert.Equal(first.Path, second.Path);
        Assert.True(second.CacheHit);
        File.WriteAllText(first.Path, "broken");
        Assert.False(pack.Generate(text, Local).CacheHit);
        // Updating cloud content invalidates the combined cache.
        pack = Create(Entry("米处，有测速照相，限速", [6, 0]), Entry("公里。", [5, 0]));
        Assert.NotEqual(first.Path, pack.Generate(text, Local).Path);
    }

    [Fact]
    public void SpecialDistanceWithoutLimitUsesNumberAndCloudTemplate()
    {
        var pack = Create(Entry("米处，有测速照相。", [4, 0]));
        var result = pack.Generate("三百米处，有测速照相。", Local);
        Assert.Equal("三百", Assert.Single(localRequests));
        Assert.Equal(new byte[] { 9, 0, 4, 0 }, PcmAudio.Read(result.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrEscapingManifestFallsBackToWholeLocalSentence(bool traversal)
    {
        const string text = "五百米处，有测速照相。";
        var entry = Entry(text, [1, 0]);
        if (traversal) entry = entry with { File = "../outside.wav" };
        else File.WriteAllText(Path.Combine(Pack, entry.File), "corrupt");
        var pack = Create(entry);
        var result = pack.Generate(text, Local);
        Assert.Equal(text, Assert.Single(localRequests));
        Assert.StartsWith(Cache, result.Path);
    }

    [Fact]
    public void MissingFragmentsOrPackUseWholeLocalSentence()
    {
        var pack = Create(Entry("公里。", [1, 0]));
        const string text = "五百米处，有测速照相，限速六十五公里。";
        pack.Generate(text, Local);
        Assert.Equal(text, Assert.Single(localRequests));
        localRequests.Clear();
        new SpeechPack(null, Cache).Generate(text, Local);
        Assert.Equal(text, Assert.Single(localRequests));
    }

    [Fact]
    public void InvalidPcmDoesNotCreateOutputAndInvalidWaveIsRejected()
    {
        string path = Path.Combine(root, "bad.wav");
        Assert.Throws<InvalidDataException>(() => PcmAudio.Write(path, [1]));
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "not-wave");
        Assert.Throws<InvalidDataException>(() => PcmAudio.Read(path));
    }

    [Theory]
    [InlineData(10, "十")]
    [InlineData(20, "二十")]
    [InlineData(65, "六十五")]
    [InlineData(100, "一百")]
    [InlineData(105, "一百零五")]
    [InlineData(110, "一百一十")]
    public void PackNumbersAndAllCommonCombinationsAreWellFormed(int number, string expected)
    {
        Assert.Equal(expected, SpeechPack.Number(number));
        Assert.Equal(29, SpeechPack.DefaultTexts().Distinct().Count());
        Assert.Contains("二百米处，有测速照相，限速九十公里。", SpeechPack.DefaultTexts());
    }

    [Fact]
    public void NullManifestEntriesFallBackWithoutCrashingTheWorker()
    {
        Directory.CreateDirectory(Pack);
        File.WriteAllText(Path.Combine(Pack, "manifest.json"), """{"Schema":1,"Model":"test","Voice":"test","Entries":null}""");
        new SpeechPack(Pack, Cache).Generate("前方有测速照相。", Local);
        Assert.Equal("前方有测速照相。", Assert.Single(localRequests));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
