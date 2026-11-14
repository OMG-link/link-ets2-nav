using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Runtime;

/// <summary>Validate small pre-generated packs before planning, so first-frame alerts need no worker round trip.</summary>
public static class PreGeneratedSpeech
{
    public static IReadOnlyDictionary<string, string> Load(string directory, Action<string>? log = null,int maximumEntries=256)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest)) return result;
        try
        {
            if (new FileInfo(manifest).Length > (maximumEntries>256?2097152:262144)) throw new InvalidDataException("Speech pack manifest too large.");
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            if (root.GetProperty("Schema").GetInt32() != 1) throw new InvalidDataException("Unsupported speech pack schema.");
            var entries = root.GetProperty("Entries");
            if (entries.GetArrayLength() > maximumEntries) throw new InvalidDataException("Too many speech pack entries.");
            foreach (var entry in entries.EnumerateArray())
            {
                string text = entry.GetProperty("Text").GetString() ?? "";
                if (text.Length is < 1 or > 2048) throw new InvalidDataException("Invalid speech pack text.");
                string file = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + ".wav";
                if (entry.GetProperty("File").GetString() != file) throw new InvalidDataException("Invalid speech pack filename.");
                string path = Path.Combine(directory, file);
                if (new FileInfo(path).Length is < 44 or > 67108864) throw new InvalidDataException("Invalid speech pack audio length.");
                byte[] audio = File.ReadAllBytes(path);
                if (Convert.ToHexString(SHA256.HashData(audio)) != entry.GetProperty("Sha256").GetString()
                    || !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                    throw new InvalidDataException("Speech pack checksum or WAV header mismatch.");
                result.Add(text, path);
            }
            log?.Invoke("预生成语音已预载：" + result.Count + " 条，首次触发可直接播放。");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            log?.Invoke("预生成语音预载失败，使用本地合成或提示音：" + ex.GetType().Name);
            return new Dictionary<string, string>();
        }
    }
}
