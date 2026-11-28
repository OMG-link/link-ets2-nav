using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaOnnx;

namespace LinkEts2Nav.Speech;

internal sealed class UnavailableModelResource(string path, Exception? readError = null)
    : Exception(readError?.Message ?? "Required model resource missing or empty: " + path, readError)
{
    internal string ResourcePath { get; } = path;
}

internal sealed class KokoroSynthesis : IDisposable
{
    private static readonly string[] ModelFiles =
        ["model.onnx", "voices.bin", "tokens.txt", "lexicon-us-en.txt", "lexicon-zh.txt"];
    private readonly WorkerOptions options;
    private readonly string fingerprint;
    private OfflineTts? model;
    internal string Fingerprint => fingerprint;

    public KokoroSynthesis(WorkerOptions options)
    {
        this.options = options;
        var files = ModelFiles.Select(name =>
        {
            string path = Path.Combine(options.ModelDirectory, name);
            try
            {
                using var file = File.OpenRead(path);
                if (file.Length == 0) throw new UnavailableModelResource(path);
                return new { name, length = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(file)) };
            }
            catch (FileNotFoundException) { throw new UnavailableModelResource(path); }
            catch (DirectoryNotFoundException) { throw new UnavailableModelResource(path); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            { throw new UnavailableModelResource(path, ex); }
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
        string key = CacheKey(text);
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
            SpeechPackFormat.ValidateWave(temporary);
            var entry = new CacheEntry(key, fingerprint, text, options.Voice, 36, SpeechPackFormat.HashFile(temporary));
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

    internal string? FindCached(string text)
    {
        string key = CacheKey(text);
        string path = Path.Combine(options.CacheDirectory, key + ".wav");
        return IsCached(path, Path.ChangeExtension(path, ".json"), key) ? path : null;
    }

    private string CacheKey(string text) => HashText(JsonSerializer.Serialize(new { fingerprint, text }));

    private bool IsCached(string path, string metadataPath, string key)
    {
        try
        {
            if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > 16384) return false;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metadataPath));
            if (entry is null || entry.Key != key || entry.Fingerprint != fingerprint) return false;
            SpeechPackFormat.VerifyWave(path, entry.Sha256);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    private string Resource(string name) => Path.Combine(options.ModelDirectory, name);
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public void Dispose() => model?.Dispose();
    private sealed record CacheEntry(string Key, string Fingerprint, string Text, string Voice, int SpeakerId, string Sha256);
}
