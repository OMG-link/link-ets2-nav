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
    public static readonly int[] CommonLimits = [30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130];
    public static IEnumerable<string> DefaultTexts()
    {
        foreach (string distance in new[] { "五百", "二百" })
        {
            yield return distance + "米处，有测速照相。";
            foreach (int speed in CommonLimits)
                yield return distance + "米处，有测速照相，限速" + Number(speed) + "公里。";
        }
        yield return "前方有测速照相。";
        yield return "米处，有测速照相。";
        yield return "米处，有测速照相，限速";
        yield return "公里。";
    }

    public static string Number(int number)
    {
        if (number is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(number));
        const string digits = "零一二三四五六七八九";
        string text = number >= 100 ? digits[number / 100] + "百" : "";
        int rest = number % 100;
        if (number >= 100 && rest is > 0 and < 10) text += "零";
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

            // Capture the spoken number itself: no guessed speed limit or grammar rewriting.
            var match = Regex.Match(text, @"^([零一二三四五六七八九十百0-9]+)米处，有测速照相(?:，限速([零一二三四五六七八九十百0-9]+)公里)?。$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return local(text);
            string middleText = match.Groups[2].Success ? "米处，有测速照相，限速" : "米处，有测速照相。";
            var middle = pack.Entries.SingleOrDefault(e => e.Text == middleText);
            var suffix = pack.Entries.SingleOrDefault(e => e.Text == "公里。");
            if (middle is null || (match.Groups[2].Success && suffix is null)) return local(text);
            // Validate every cloud fragment before spending time on local inference.
            string middlePath = Resolve(middle);
            string? suffixPath = match.Groups[2].Success ? Resolve(suffix!) : null;
            var identity = JsonSerializer.Serialize(new { schema = 1, text, pack.Model, pack.Voice,
                middle.Sha256, suffix = suffixPath is null ? null : suffix!.Sha256 });
            var paths = new List<string> { local(match.Groups[1].Value).Path, middlePath };
            if (suffixPath is not null) { paths.Add(local(match.Groups[2].Value).Path); paths.Add(suffixPath); }
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
