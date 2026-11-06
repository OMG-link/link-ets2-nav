using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaOnnx;

namespace LinkEts2Nav.Speech;

internal sealed class KokoroSynthesis : IDisposable
{
    private static readonly string[] ModelFiles =
        ["model.onnx", "voices.bin", "tokens.txt", "lexicon-us-en.txt", "lexicon-zh.txt"];
    private readonly WorkerOptions options;
    private readonly string fingerprint;
    private OfflineTts? model;

    public KokoroSynthesis(WorkerOptions options)
    {
        this.options = options;
        var files = ModelFiles.Select(name =>
        {
            string path = Path.Combine(options.ModelDirectory, name);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new FileNotFoundException("Required model resource missing or empty: " + name, path);
            return new { name, length = file.Length, sha256 = HashFile(path) };
        }).ToArray();
        // Stable content keys bind the text, selected voice, tokenizer/lexicons,
        // synthesis settings and engine. Moving the same model does not invalidate cache.
        fingerprint = HashText(JsonSerializer.Serialize(new
        {
            schema = 1, engine = "sherpa-onnx-2.0.1", preset = "kokoro-multi-lang-v1_1",
            options.Voice, speakerId = 36, speed = 1.0f, options.Threads, files
        }));
        Directory.CreateDirectory(options.CacheDirectory);
    }

    public (string Path, bool CacheHit) Generate(string text)
    {
        string key = HashText(JsonSerializer.Serialize(new { fingerprint, text }));
        string path = Path.Combine(options.CacheDirectory, key + ".wav");
        string metadataPath = Path.Combine(options.CacheDirectory, key + ".json");
        if (IsCached(path, metadataPath, key)) return (path, true);
        if (model is null)
        {
            var config = new OfflineTtsConfig();
            config.Model.Kokoro.Model = Resource("model.onnx");
            config.Model.Kokoro.Voices = Resource("voices.bin");
            config.Model.Kokoro.Tokens = Resource("tokens.txt");
            config.Model.Kokoro.Lexicon = Resource("lexicon-us-en.txt") + "," + Resource("lexicon-zh.txt");
            config.Model.NumThreads = options.Threads;
            config.Model.Provider = "cpu";
            model = new OfflineTts(config);
        }
        var audio = model.Generate(text, 1.0f, 36);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp.wav";
        string temporaryMetadata = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var samples = audio.Samples;
            if (audio.SampleRate <= 0 || samples.Length == 0 || samples.Any(sample => !float.IsFinite(sample)))
                throw new InvalidDataException("Synthesizer returned invalid audio samples.");
            if (!audio.SaveToWaveFile(temporary)) throw new IOException("Unable to save synthesized WAV.");
            if (!WaveFile.IsValid(temporary)) throw new InvalidDataException("Synthesizer did not produce a valid PCM WAV.");
            var entry = new CacheEntry(key, fingerprint, text, options.Voice, 36, HashFile(temporary));
            File.WriteAllText(temporaryMetadata, JsonSerializer.Serialize(entry), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            File.Move(temporaryMetadata, metadataPath, overwrite: true);
            return (path, false);
        }
        finally
        {
            audio.Dispose();
            // These are unique files created by this request in the configured cache.
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporaryMetadata)) File.Delete(temporaryMetadata);
        }
    }

    private bool IsCached(string path, string metadataPath, string key)
    {
        try
        {
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 16384 || !WaveFile.IsValid(path)) return false;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
            return entry is not null && entry.Key == key && entry.Fingerprint == fingerprint
                && StringComparer.OrdinalIgnoreCase.Equals(entry.Sha256, HashFile(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private string Resource(string name) => Path.Combine(options.ModelDirectory, name);
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public void Dispose() => model?.Dispose();
    private sealed record CacheEntry(string Key, string Fingerprint, string Text, string Voice, int SpeakerId, string Sha256);
}

internal static class WaveFile
{
    public static bool IsValid(string path)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        if (stream.Length < 44 || stream.Length > 64 * 1024 * 1024) return false;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952 || (long)reader.ReadUInt32() + 8 != stream.Length
            || reader.ReadUInt32() != 0x45564157) return false;
        bool format = false, data = false;
        ushort blockAlign = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            uint name = reader.ReadUInt32(), size = reader.ReadUInt32();
            long next = stream.Position + size;
            if (next > stream.Length) return false;
            if (name == 0x20746D66)
            {
                if (size < 16 || reader.ReadUInt16() != 1) return false;
                ushort channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32(), byteRate = reader.ReadUInt32();
                blockAlign = reader.ReadUInt16();
                ushort bits = reader.ReadUInt16();
                if (channels != 1 || rate != 24000 || bits != 16 || blockAlign != 2 || byteRate != rate * blockAlign) return false;
                format = true;
            }
            else if (name == 0x61746164)
            {
                if (!format || size < blockAlign || size % blockAlign != 0) return false;
                data = true;
            }
            stream.Position = next + (size & 1);
        }
        return format && data && stream.Position == stream.Length;
    }
}
