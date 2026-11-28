using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class JourneySpeechPackTests : IDisposable
{
    private const string Example = "准备出发，全程一点二公里，大约需要两分钟。";
    private readonly string root = Directory.CreateTempSubdirectory("nav-journey-").FullName;
    private string Journey => Path.Combine(root, "Journey");
    private string Cache => Path.Combine(root, "cache");
    private static readonly string[] Base = new[] { "准备出发，", "重新规划路线，", "全程", "米，", "公里，", "大约需要", "分钟。" }
        .Concat("零一二三四五六七八九两十百千万亿点".Select(c => c.ToString())).ToArray();

    private static PackEntry Write(string folder, string text, byte sample)
    {
        string file = SpeechPack.FileName(text), path = Path.Combine(folder, file);
        PcmAudio.Write(path, [sample, 0]);
        return new(text, file, SpeechPack.Hash(File.ReadAllBytes(path)));
    }

    private static void Save(string folder, params PackEntry[] entries) =>
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PackManifest(1, "test", "voice", entries)));

    private PackEntry[] WriteBase() => Base.Select((t, i) => Write(Journey, t, (byte)(i + 1))).ToArray();
    private static byte[] Expected(params string[] pieces) =>
        pieces.SelectMany(t => new[] { checked((byte)(Array.IndexOf(Base, t) + 1)), (byte)0 }).ToArray();
    private static (string, bool) NoModel(string text) => throw new Exception("Unexpected TTS for " + text);

    [Fact]
    public async Task UnseenJourneyUsesTheTwoNaturalClausesWithoutAModelThenAnExactFileWins()
    {
        var entries = WriteBase();
        Save(Journey, [.. entries, Write(Journey, "全程一点二公里，", 80), Write(Journey, "大约需要两分钟。", 81)]);
        await using var worker = new KokoroSpeechProcess(new SpeechOptions { Enabled = true, WorkerPath = typeof(SpeechPack).Assembly.Location,
            ModelDirectory = Path.Combine(root, "absent-model"), Voice = "zf_067", Threads = 1, PackDirectory = root, CacheDirectory = Cache });
        var token = TestContext.Current.CancellationToken;
        string text = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Start, 1200, 100);
        Assert.Equal(Example, text);
        string first = await worker.SynthesizeAsync(text, token);
        Assert.StartsWith("journey-", Path.GetFileName(first));
        Assert.False(worker.LastCacheHit);
        Assert.Equal(new byte[] { 1, 0, 80, 0, 81, 0 }, PcmAudio.Read(first));
        Assert.Equal(first, await worker.SynthesizeAsync(text, token));
        Assert.True(worker.LastCacheHit);
        Save(root, Write(root, text, 99));
        Assert.Equal(new byte[] { 99, 0 }, PcmAudio.Read(await worker.SynthesizeAsync(text, token)));
    }

    [Fact]
    public void CorruptOrForeignRecordingsCannotBeReplacedByAStringOfIndividualDigits()
    {
        var baseEntries = WriteBase();
        var clause = Write(Journey, "全程一点二公里，", 90);
        Save(Journey, [.. baseEntries, clause]);
        PcmAudio.Write(Path.Combine(Journey, clause.File), [1, 0]);
        var pack = new SpeechPack(root, Cache);
        var calls = new List<string>();
        (string, bool) Local(string text) { calls.Add(text); return ("fallback", false); }
        Assert.Equal("fallback", pack.Generate(Example, Local).Path);
        foreach (var entry in new[] { Write(Journey, "两分钟", 12) with { VoiceProfile = "another-voice" },
                     Write(Journey, "两分钟", 12) with { VoiceProfile = "" }, Write(Journey, "两分钟", 12) with { Composed = true } })
        {
            Save(Journey, [.. baseEntries, entry]);
            Assert.Equal("fallback", pack.Generate(Example, Local).Path);
        }
        File.Delete(Path.Combine(Journey, "manifest.json"));
        Assert.Equal("fallback", pack.Generate(Example, Local).Path);
        Assert.Equal(Enumerable.Repeat(Example, 5), calls);
    }

    [Fact]
    public void UsesAvailableCompleteNumberAndInvalidatesTheCacheWhenARecordingChanges()
    {
        string text = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Replanned, 2050, 101 * 60);
        var entries = WriteBase();
        var time = Write(Journey, "一百零一", 77);
        var distance = Write(Journey, "二点一公里", 74);
        Save(Journey, [.. entries, distance, time]);
        var pack = new SpeechPack(root, Cache);
        var first = pack.Generate(text, NoModel);
        byte[] expected = [.. Expected("重新规划路线，", "全程"), 74, 0, .. Expected("大约需要"), 77, 0, .. Expected("分钟。")];
        Assert.Equal(expected, PcmAudio.Read(first.Path));
        var updated = Write(Journey, "一百零一", 78);
        Save(Journey, [.. entries, distance, updated]);
        var changed = pack.Generate(text, NoModel);
        Assert.NotEqual(first.Path, changed.Path);
        expected[^4] = 78;
        Assert.Equal(expected, PcmAudio.Read(changed.Path));
    }

    [Theory]
    [InlineData(22, 0)]
    [InlineData(122, 82)]
    [InlineData(222, 83)]
    public void FormattedJourneysKeepWholeQuantitiesAndTheLongestMatchingMinuteEnding(int minutes, int leading)
    {
        var entries = WriteBase();
        var corrupt = Write(Journey, "一百二十二分钟。", 90);
        Save(Journey, [.. entries, Write(Journey, "三点二公里", 80), Write(Journey, "二十二分钟", 81),
            Write(Journey, "一百", 82), Write(Journey, "两百", 83), corrupt]);
        PcmAudio.Write(Path.Combine(Journey, corrupt.File), [99, 0]);
        string text = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Replanned, 3200, minutes * 60);
        var actual = PcmAudio.Read(new SpeechPack(root, Cache).Generate(text, NoModel).Path);
        byte[] head = [.. Expected("重新规划路线，", "全程"), 80, 0, .. Expected("大约需要")];
        byte[] expected = leading == 0 ? [.. head, 81, 0] : [.. head, (byte)leading, 0, 81, 0];
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyOneMissingNaturalClauseUsesTheLiveVoiceAndTheOtherRemainsIntact(bool missingTime)
    {
        var entries = WriteBase();
        const string distance = "全程三点七公里，", duration = "大约需要97531分钟。";
        string prompt = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Start, 3700, 97531 * 60d);
        Assert.Equal("准备出发，" + distance + duration, prompt);
        string spokenDuration = JourneySpeechText.Parse(prompt)!.TimeClause;
        var good = Write(Journey, missingTime ? distance : spokenDuration, 70);
        Save(Journey, [.. entries, good]);
        var pack = new SpeechPack(root, Cache);
        string localPath = Path.Combine(Cache, "local.wav");
        var requests = new List<string>();
        (string, bool) Local(string text) { requests.Add(text); PcmAudio.Write(localPath, [99,0]); return (localPath, false); }
        Assert.Equal("PreparedSpeechUnavailableException", Record.Exception(() => pack.Generate(prompt, NoModel, true))?.GetType().Name);
        var result = pack.Generate(prompt, Local);
        Assert.StartsWith("journey-hybrid-", Path.GetFileName(result.Path));
        Assert.Equal(missingTime ? new byte[] { 1,0,70,0,99,0 } : [1,0,99,0,70,0], PcmAudio.Read(result.Path));
        Assert.Equal(new[] { missingTime ? duration : distance }, requests);
        string nav = Path.Combine(root, "Navigation");
        Save(nav, Write(nav, prompt, 92));
        Assert.Equal(new byte[] { 92,0 }, PcmAudio.Read(pack.Generate(prompt, NoModel).Path));
    }

    [Fact]
    public void EveryFormattedNumberThrough9999AndAllMeterRoundingBoundariesCanBeRecorded()
    {
        for (int i = 0; i <= 9999; i++)
        {
            string prompt = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Start, i * 1000.0 + 100, i * 60.0);
            var parsed = Assert.IsType<JourneySpeechText>(JourneySpeechText.Parse(prompt));
            Assert.All(parsed.DistanceNumber + parsed.Minutes, c => Assert.Contains(c, JourneySpeechText.NumberSounds));
            if (i <= 1000)
                Assert.NotNull(JourneySpeechText.Parse(NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Replanned, i, 0)));
        }
        Assert.NotNull(JourneySpeechText.Parse(NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Start, 0, 0)));
        Assert.Null(JourneySpeechText.Parse("准备出发，全程十十点七公里，大约需要两分钟。"));
        Assert.Null(JourneySpeechText.Parse("准备出发，全程半公里，大约需要几分钟。"));
    }

    [Theory]
    [InlineData(10000, "一万")]
    [InlineData(10010, "一万零一十")]
    [InlineData(100010001, "一亿零一万零一")]
    [InlineData(2147483647, "二十一亿四千七百四十八万三千六百四十七")]
    public void ArabicLargeValuesUseTheirChinesePlaceValues(int number, string spoken)
    {
        var text = NavigationJourneyPrompts.Format(NavigationJourneyNoticeKind.Replanned, number * 1000.0 + 900, number * 60.0);
        var parsed = Assert.IsType<JourneySpeechText>(JourneySpeechText.Parse(text));
        Assert.Equal(spoken + "点九", parsed.DistanceNumber);
        Assert.Equal(spoken, parsed.Minutes);
    }

    public void Dispose() => Directory.Delete(root, true);
}
