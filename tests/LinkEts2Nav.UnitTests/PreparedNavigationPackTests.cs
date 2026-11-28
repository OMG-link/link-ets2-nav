using System.Runtime.InteropServices;
using System.Text.Json;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class PreparedNavigationPackTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("nav-prepared-").FullName;
    private string Navigation => Path.Combine(root, "Navigation");
    private string Journey => Path.Combine(root, "Journey");
    private SpeechPack Reader => new(root, Path.Combine(root, "cache"));
    private static (string Path, bool CacheHit) NoLive(string text) => throw new Exception("Unexpected live synthesis: " + text);

    private static PackEntry Clip(string directory, string text, params short[] samples)
    {
        string filename = SpeechPack.FileName(text), path = Path.Combine(directory, filename);
        PcmAudio.Write(path, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
        return new(text, filename, SpeechPack.Hash(File.ReadAllBytes(path)));
    }
    private static void Manifest(string directory, IEnumerable<PackEntry> entries, string voice = "one", string? profile = "profile")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new PackManifest(1, "model", voice, entries.ToArray())
            { ApiProfile = profile }));
    }
    private short[] Audio(string text)
    {
        var result = Reader.Generate(text, NoLive, preparedOnly: true);
        return MemoryMarshal.Cast<byte, short>(PcmAudio.Read(result.Path)).ToArray();
    }
    private void Missing(string text)
    {
        var error = Record.Exception(() => Reader.Generate(text, NoLive, preparedOnly: true));
        Assert.Equal("PreparedSpeechUnavailableException", error?.GetType().Name);
    }

    [Fact]
    public void EveryDirectionLaneAndNumberMustBeCoveredAndListAndClausePausesAreAudible()
    {
        var entries = new[] { "左转", "然后", "走右侧第", "第三", "车道" }.Select((t, i) => Clip(Navigation, t, (short)(500 + i))).ToArray();
        Manifest(Navigation, entries);
        Manifest(Journey, [Clip(Journey, "一", 601), Clip(Journey, "三", 603)]);
        const string text = "左转，然后走右侧第一、第三车道。";
        short[] expected = [500, .. new short[3600], 501, 502, 601, .. new short[2400], 503, 504];
        Assert.Equal(expected, Audio(text));
        Assert.True(Reader.Generate(text, NoLive, true).CacheHit);
        Missing(text.Replace("右侧", "左侧"));
        Missing(text.Replace("第三", "第四"));
        Missing("左转，然后走右侧第一、第三车道，注意右侧来车。");
        Assert.Equal(("local", false), Reader.Generate("右转。", _ => ("local", false)));
    }

    [Fact]
    public void ReusesASpokenPauseAndKeepsALongRecordingWithItsOwnPunctuationIntact()
    {
        short[] left = [501, .. new short[2000]], right = [.. new short[2000], 503];
        Manifest(Navigation, [Clip(Navigation, "左转，", left), Clip(Navigation, "注意右侧来车。", right),
            Clip(Navigation, "有多个相近出口，请", 509), Clip(Navigation, "向右前方行驶。", 511)]);
        Assert.Equal(left.Concat(right), Audio("左转，注意右侧来车。"));
        Assert.Equal(new short[] { 509, 511 }, Audio("有多个相近出口，请向右前方行驶。"));
        Missing("有多个相近出口，请向左前方行驶。");
    }

    [Fact]
    public void ChoosesACompleteCoverAndRetriesValidShorterClipsIfAPreferredWaveIsDamaged()
    {
        var entries = new[] { "即将走右侧", "即将", "走右侧第十车道", "即将走右侧第十车道" }
            .Select((t, i) => Clip(Navigation, t, (short)(700 + i))).ToArray();
        Manifest(Navigation, entries);
        const string text = "即将走右侧第十车道。";
        Assert.Equal(new short[] { 703 }, Audio(text));
        File.WriteAllText(Path.Combine(Navigation, entries[3].File), "damaged");
        Assert.Equal(new short[] { 701, 702 }, Audio(text));
        File.Delete(Path.Combine(Navigation, entries[1].File));
        Missing(text);
    }

    [Fact]
    public void SharesVerifiedNumericClipsOnlyWithTheSameAcousticProfile()
    {
        const string text = "沿当前道路，继续行驶10005公里。";
        Manifest(Navigation, [Clip(Navigation, "沿当前道路，继续行驶", 501), Clip(Navigation, "公里", 502)]);
        var digits = new[] { "一", "万", "零", "五" }.Select((t, i) => Clip(Journey, t, (short)(600 + i))).ToArray();
        var number = Clip(Journey, "一万零五", 670);
        Manifest(Journey, [.. digits, number], profile: "different");
        Missing(text);
        Manifest(Journey, [.. digits, number], voice: "different");
        Missing(text);
        Manifest(Journey, [.. digits, number with { VoiceProfile = "" }]);
        Missing(text);
        Manifest(Journey, [.. digits, number with { Composed = true }]);
        Missing(text);
        Manifest(Journey, digits);
        Missing(text);
        Assert.Equal(("live", false), Reader.Generate(text, t => { Assert.Equal(text, t); return ("live", false); }));
        Manifest(Journey, [.. digits, number]);
        Assert.Equal(new short[] { 501, 670, 502 }, Audio(text));
        Manifest(Navigation, [Clip(Navigation, "左", 701), Clip(Navigation, "转", 702)]);
        Missing("左转。");
    }

    [Fact]
    public void UnknownOrMismatchedNamesKeepThePrecedingTurnAndDistanceAndExactNameRecordingsWin()
    {
        string places = Path.Combine(root, "CityNames");
        const string company = "右转，然后九十米到达North Quay货场。";
        string[] terms = ["右转", "然后", "九十米", "到达目的地", "North Quay", "到达", "货场", "进入城区",
            "请注意减速慢行", "进入码头", "按导航方向乘坐轮渡", "后"];
        Manifest(Navigation, terms.Select((s, i) => Clip(Navigation, s, (short)(700 + i))));
        short[] Markers(string text) => Audio(text).Where(s => s != 0).ToArray();
        Assert.Equal(new short[] { 700, 701, 702, 703 }, Markers(company));
        Assert.Equal(new short[] { 702, 711, 707, 708 }, Markers("九十米后，进入未知地城区，请注意减速慢行。"));
        Assert.Equal(new short[] { 702, 711, 709, 710 }, Markers("九十米后，进入A码头，乘坐前往B的轮渡。"));
        var name = Clip(places, "North Quay", 927);
        Manifest(places, [name], voice: "another");
        Assert.Equal(new short[] { 700, 701, 702, 703 }, Markers(company));
        Manifest(places, [name]);
        Assert.Equal(new short[] { 700, 701, 702, 705, 927, 706 }, Markers(company));
        var punctuated = Clip(places, "北港，旧库", 928);
        Manifest(places, [name, punctuated]);
        Assert.Equal(new short[] { 700, 701, 702, 705, 928, 706 }, Markers(company.Replace("North Quay", "北港，旧库")));
        Manifest(root, [Clip(root, company, 999)], voice: "picked-recording");
        Assert.Equal(new short[] { 999 }, Audio(company));
    }

    [Theory]
    [InlineData("到达North Quay货场。", "到达", "North Quay", "货场", "到达目的地。")]
    [InlineData("进入Eindhoven城区，请注意减速慢行。", "进入", "Eindhoven", "城区，请注意减速慢行", "进入城区，请注意减速慢行。")]
    public void NormalModeUsesTheWholeNamedWordingAvailableInOneNavigationVoice(string text, string prefix, string name, string suffix, string generic)
    {
        Manifest(Navigation, [Clip(Navigation, prefix, 501), Clip(Navigation, name, 502), Clip(Navigation, suffix, 503),
            Clip(Navigation, generic, 700)]);
        var result = Reader.Generate(text, NoLive);
        Assert.Equal(new short[] { 501, 502, 503 }, MemoryMarshal.Cast<byte, short>(PcmAudio.Read(result.Path)).ToArray());
        Assert.Equal(new short[] { 700 }, Audio(text)); // The stricter mode retains its registered-name requirement.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalModePreservesTheRegisteredNameAndOnlySynthesizesMissingCarrierAudio(bool recordedCarriers)
    {
        const string prefix = "进入", suffix = "城区，请注意减速慢行。", generic = prefix + suffix;
        const string name = "Húsavík";
        string places = Path.Combine(root, "CityNames");
        Manifest(places, [Clip(places, name, 904)], voice: "recorded-name");
        var navigation = new List<PackEntry> { Clip(Navigation, generic, 700) };
        if (recordedCarriers) navigation.AddRange([Clip(Navigation, prefix, 501), Clip(Navigation, suffix, 502)]);
        Manifest(Navigation, navigation);
        var live = new List<string>();
        (string Path, bool CacheHit) Local(string cue)
        {
            Assert.Contains(cue, new[] { prefix, suffix });
            live.Add(cue);
            string dir = Path.Combine(root, "live");
            var entry = Clip(dir, cue, (short)(cue == prefix ? 501 : 502));
            return (Path.Combine(dir, entry.File), false);
        }
        var named = Reader.Generate(prefix + name + suffix, Local);
        Assert.Equal(new short[] { 501, 904, 502 }, MemoryMarshal.Cast<byte, short>(PcmAudio.Read(named.Path)).ToArray());
        Assert.Equal(recordedCarriers ? Array.Empty<string>() : [prefix, suffix], live);
        var missing = Reader.Generate(prefix + "未登记" + suffix, NoLive);
        Assert.Equal(new short[] { 700 }, MemoryMarshal.Cast<byte, short>(PcmAudio.Read(missing.Path)).ToArray());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
