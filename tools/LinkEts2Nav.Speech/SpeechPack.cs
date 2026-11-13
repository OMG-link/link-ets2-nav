using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkEts2Nav.Speech;

public sealed record PackEntry(string Text, string File, string Sha256);
public sealed record PackManifest(int Schema, string Model, string Voice, PackEntry[] Entries);

/// <summary>Only explicitly generated pack entries can override the local voice.</summary>
public sealed class SpeechPack(string? directory, string cacheDirectory)
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string FileName(string text) => Hash(Encoding.UTF8.GetBytes(text)) + ".wav";
    public static readonly int[] CommonLimits = [30, 40, 50, 60, 70, 80, 90, 100];
    public const string UncalibratedText = "正靠近未校准的探头，无法获取其监测方向，请减速慢行。";
    public static IEnumerable<string> DefaultTexts()
    {
        foreach (string prefix in new[] { "五百米处，有测速照相", "两百米处，有测速照相", "两百米内，有测速照相", "前方有测速照相" })
        {
            yield return prefix + "。";
            foreach (int speed in CommonLimits)
                yield return prefix + "，限速" + Number(speed) + "。";
        }
        yield return "米处，有测速照相。";
        yield return "米处，有测速照相，限速";
        yield return "米内，有测速照相。";
        yield return "米内，有测速照相，限速";
        yield return "前方有测速照相，限速";
        yield return UncalibratedText;
        yield return "在听到下一条语音提示前，请沿当前道路行驶。";
        yield return "导航开始，请按规划路线行驶。";
        yield return "目的地已更新，请按新的路线行驶。";
        yield return "路线已重新规划，请按新的路线行驶。";
        yield return "您已到达目的地，本次导航结束。";
        foreach(int meters in Enumerable.Range(1,10).Select(n=>n*50))
            yield return NavigationDistance(meters)+"后，进入城区，请注意减速慢行。";
        yield return "即将进入城区，请注意减速慢行。";


        foreach (string action in new[] { "左转", "右转", "向左前方行驶", "向右前方行驶", "直行", "掉头", "向左前方进入主路", "向右前方进入主路", "进入环岛", "驶出环岛", "经过出口", "到达目的地附近" })
        {
            foreach (int distance in Enumerable.Range(1, 10).Select(n => n * 50).Concat(new[] { 1000, 2000 }))
                yield return NavigationDistance(distance) + "后，" + action + "。";
            yield return action == "直行" ? "保持直行。" : "即将" + action + "。";
            yield return action=="到达目的地附近"?"目的地就在附近，请留意停车位置。":action + "。";
        }
    }

    public static string NavigationDistance(int meters)
    {
        if (meters < 1000) return Number(meters).Replace("二百", "两百", StringComparison.Ordinal) + "米";
        const string digits = "零一二三四五六七八九";
        string text = Number(meters / 1000);
        string fraction = (meters % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        if (fraction.Length > 0) text += "点" + string.Concat(fraction.Select(c => digits[c - '0']));
        if (fraction.Length == 0) text = meters / 1000 == 2 ? "两" : text.Replace("二百", "两百", StringComparison.Ordinal);
        return text + "公里";
    }

    public static string Number(int number)
    {
        if (number is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(number));
        const string digits = "零一二三四五六七八九";
        string text = number >= 1000 ? digits[number / 1000] + "千" : "";
        int hundreds = number / 100 % 10;
        if (hundreds > 0) text += digits[hundreds] + "百";
        else if (number >= 1000 && number % 100 > 0) text += "零";
        int rest = number % 100;
        if (hundreds > 0 && rest is > 0 and < 10) text += "零";
        if (rest >= 10) text += (rest / 10 == 1 && number < 100 ? "" : digits[rest / 10].ToString()) + "十";
        if (rest % 10 != 0) text += digits[rest % 10];
        return text;
    }

    public (string Path, bool CacheHit) Generate(string text, Func<string, (string Path, bool CacheHit)> local)
    {
        if (directory is null || !File.Exists(Path.Combine(directory, "manifest.json"))) return local(text);
        PackManifest pack;
        try
        {
            string manifest = Path.Combine(directory, "manifest.json");
            if (new FileInfo(manifest).Length > 262144) throw new InvalidDataException("Speech pack manifest is too large.");
            pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException("Empty speech pack.");
            if (pack.Schema != 1 || pack.Entries is null || pack.Entries.Length > 256 || pack.Entries.Any(e => e is null)) throw new InvalidDataException("Unsupported speech pack.");
            var exact = pack.Entries.SingleOrDefault(e => e.Text == text);
            if (exact is not null)
            {
                string source = Resolve(exact);
                string target = Path.Combine(cacheDirectory, "qwen-" + exact.Sha256 + ".wav");
                if (File.Exists(target) && Hash(File.ReadAllBytes(target)) == exact.Sha256) return (target, true);
                PcmAudio.Write(target, PcmAudio.Read(source));
                return (target, false);
            }

            // Capture spoken numbers without inventing a speed limit.
            var match = Regex.Match(text, @"^(?:(?<distance>[零一二两三四五六七八九十百0-9]+)(?<where>米处|米内)，有测速照相|(?<ahead>前方有测速照相))(?:，限速(?<speed>[零一二两三四五六七八九十百0-9]+))?。$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return local(text);
            bool hasSpeed = match.Groups["speed"].Success;
            string middleText = match.Groups["ahead"].Success ? "前方有测速照相" :
                match.Groups["where"].Value + "，有测速照相";
            middleText += hasSpeed ? "，限速" : "。";
            var middle = pack.Entries.SingleOrDefault(e => e.Text == middleText);
            if (middle is null) return local(text);
            // Validate the cloud fragment before spending time on local inference.
            string middlePath = Resolve(middle);
            var identity = JsonSerializer.Serialize(new { schema = 2, text, pack.Model, pack.Voice, middle.Sha256 });
            var paths = new List<string>();
            if (match.Groups["distance"].Success) paths.Add(local(match.Groups["distance"].Value).Path);
            paths.Add(middlePath);
            if (hasSpeed) paths.Add(local(match.Groups["speed"].Value).Path);
            // Hash actual local samples too, so changing the local voice invalidates the assembled file.
            var samples = paths.Select(PcmAudio.Read).ToArray();
            string key = Hash(Encoding.UTF8.GetBytes(identity + string.Join("", samples.Select(Hash))));
            string result = Path.Combine(cacheDirectory, "hybrid-" + key + ".wav");
            byte[] joined = samples.SelectMany(x => x).ToArray();
            if (File.Exists(result))
            {
                try { if (PcmAudio.Read(result).AsSpan().SequenceEqual(joined)) return (result, true); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
            PcmAudio.Write(result, joined);
            return (result, false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Speech pack unavailable; using local synthesis: " + ex.GetType().Name);
            return local(text);
        }
    }

    private string Resolve(PackEntry entry)
    {
        // Flat content-derived filenames prevent manifest path traversal.
        if (entry.File != FileName(entry.Text)) throw new InvalidDataException("Unexpected pack filename.");
        string path = Path.Combine(directory!, entry.File);
        if (new FileInfo(path).Length > 64 * 1024 * 1024 || Hash(File.ReadAllBytes(path)) != entry.Sha256)
            throw new InvalidDataException("Speech pack checksum mismatch.");
        PcmAudio.Read(path);
        return path;
    }
}
