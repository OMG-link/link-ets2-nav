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

    [Theory]
    [InlineData(1000, "一千")]
    [InlineData(1050, "一千零五十")]
    [InlineData(2000, "二千")]
    [InlineData(2150, "二千一百五十")]
    public void NavigationDistancesSupportThousands(int value, string expected)
        => Assert.Equal(expected, SpeechPack.Number(value));

    [Fact]
    public void CommonSentenceUsesCloudFileWithoutLocalInferenceAndRepairsCache()
    {
        const string text = "五百米处，有测速照相，限速九十。";
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
        var pack = Create(Entry("米处，有测速照相，限速", [3, 0, 4, 0]));
        const string text = "五百米处，有测速照相，限速六十五。";
        var first = pack.Generate(text, Local);
        Assert.Equal(new[] { "五百", "六十五" }, localRequests);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0, 9, 0 }, PcmAudio.Read(first.Path));
        var second = pack.Generate(text, Local);
        Assert.Equal(first.Path, second.Path);
        Assert.True(second.CacheHit);
        File.WriteAllText(first.Path, "broken");
        Assert.False(pack.Generate(text, Local).CacheHit);
        // Updating cloud content invalidates the combined cache.
        pack = Create(Entry("米处，有测速照相，限速", [6, 0]));
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
        var pack = Create(Entry("不相关模板。", [1, 0]));
        const string text = "五百米处，有测速照相，限速六十五。";
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
        Assert.Equal(222, SpeechPack.DefaultTexts().Distinct().Count());
        Assert.Contains("两百米处，有测速照相，限速九十。", SpeechPack.DefaultTexts());
    }

    [Fact]
    public void NullManifestEntriesFallBackWithoutCrashingTheWorker()
    {
        Directory.CreateDirectory(Pack);
        File.WriteAllText(Path.Combine(Pack, "manifest.json"), """{"Schema":1,"Model":"test","Voice":"test","Entries":null}""");
        new SpeechPack(Pack, Cache).Generate("前方有测速照相。", Local);
        Assert.Equal("前方有测速照相。", Assert.Single(localRequests));
    }
    [Theory]
    [InlineData("两百米内，有测速照相，限速一百一十。", "米内，有测速照相，限速", true)]
    [InlineData("前方有测速照相，限速一百一十。", "前方有测速照相，限速", false)]
    public void SpecialLimitsSupportLateWarningTemplates(string text, string fragment, bool distance)
    {
        var pack = Create(Entry(fragment, [3, 0]));
        var result = pack.Generate(text, Local);
        Assert.Equal(distance ? new[] { "两百", "一百一十" } : new[] { "一百一十" }, localRequests);
        Assert.Equal(distance ? new byte[] { 9, 0, 3, 0, 9, 0 } : new byte[] { 3, 0, 9, 0 }, PcmAudio.Read(result.Path));
    }

    [Fact]
    public void DefaultPackCoversAllWarningFormsWithoutUnitsAndStopsAt100()
    {
        Assert.Equal(new[] { 30, 40, 50, 60, 70, 80, 90, 100 }, SpeechPack.CommonLimits);
        var texts = SpeechPack.DefaultTexts().ToArray();
        Assert.DoesNotContain(texts, text => text.Contains("测速照相") && (text.Contains("公里") || text.Contains("一百一十")));
        foreach (string prefix in new[] { "五百米处，有测速照相", "两百米处，有测速照相", "两百米内，有测速照相", "前方有测速照相" })
            foreach (int limit in SpeechPack.CommonLimits)
                Assert.Contains(prefix + "，限速" + SpeechPack.Number(limit) + "。", texts);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
