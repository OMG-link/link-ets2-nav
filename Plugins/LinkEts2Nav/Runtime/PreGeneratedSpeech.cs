using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Runtime;

/// <summary>Verify pre-generated audio and retain only file paths for playback.</summary>
public static class PreGeneratedSpeech
{
    public static IReadOnlyDictionary<string, string> Load(string directory, Action<string>? log = null, int? maximumEntries = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = Path.Combine(directory, "manifest.json");
        FileAttributes attributes;
        try { attributes=File.GetAttributes(manifest); }
        catch(FileNotFoundException) { return result; }
        catch(DirectoryNotFoundException) { return result; }
        if((attributes&FileAttributes.Directory)!=0)throw new InvalidDataException("Speech pack manifest must be a file.");
        using var manifestStream = File.OpenRead(manifest);
        using var json = JsonDocument.Parse(manifestStream);
        var root = json.RootElement;
        if (root.GetProperty("Schema").GetInt32() != 1) throw new InvalidDataException("Unsupported speech pack schema.");
        var entries = root.GetProperty("Entries");
        if (maximumEntries is int limit && entries.GetArrayLength() > limit) throw new InvalidDataException("Too many speech pack entries.");
        Span<byte> header = stackalloc byte[12];
        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = entry.GetProperty("Text").GetString() ?? "";
            if (text.Length is < 1 or > 2048) throw new InvalidDataException("Invalid speech pack text.");
            string file = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + ".wav";
            if (entry.GetProperty("File").GetString() != file) throw new InvalidDataException("Invalid speech pack filename.");
            string path = Path.Combine(directory, file);
            using var audio = File.OpenRead(path);
            if (audio.Length is < 44 or > 67108864) throw new InvalidDataException("Invalid speech pack audio length.");
            audio.ReadExactly(header);
            audio.Position = 0;
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8)
                || Convert.ToHexString(SHA256.HashData(audio)) != entry.GetProperty("Sha256").GetString())
                throw new InvalidDataException("Speech pack checksum or WAV header mismatch.");
            result.Add(text, path);
        }
        log?.Invoke("预生成语音已预载：" + result.Count + " 条，首次触发可直接播放。");
        return result;
    }
}
