using System.Security.Cryptography;
using System.Text;
using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Speech;

// The verified paths belong to one acoustic profile. A generated full recording can also serve as
// a natural clause in another phrase. Precomposed recordings never enter this set.
internal sealed class BudgetSpeechSource : IDisposable
{
    private readonly KokoroSynthesis? local;
    private readonly SpeechApi? api;
    private readonly HttpClient? client;
    private readonly string cache;
    private readonly Dictionary<string, string> clips = new(StringComparer.Ordinal);
    internal string Profile { get; }

    internal BudgetSpeechSource(GenerationJob job)
    {
        if (job.LocalModelDirectory is { } model)
        {
            if (job.Voice != "zf_067" || job.LocalThreads is < 1 or > 8) throw new PackGenerationException("local-model");
            local = new(new(model, job.LocalCacheDirectory ?? Path.Combine(job.CacheDirectory, "Kokoro"), job.Voice, job.LocalThreads, null));
            Profile = local.Fingerprint;
        }
        else
        {
            api = SpeechApi.Create(job);
            client = new(new HttpClientHandler { AllowAutoRedirect = false }) { MaxResponseContentBufferSize = PcmAudio.MaximumWaveBytes };
            Profile = api.Profile;
        }
        cache = Path.Combine(job.CacheDirectory, Profile);
    }

    internal void AddVerified(string text, string path) => clips[text] = path;
    internal bool HasClip(string text) => clips.ContainsKey(text);

    internal async Task<long> CostAsync(string text, CancellationToken token)
    {
        if (HasClip(text)) return 0;
        if (local is not null)
        {
            if (local.FindCached(text) is { } path) { AddVerified(text, path); return 0; }
            return Encoding.UTF8.GetByteCount(text);
        }
        string candidate = Path.Combine(cache, SpeechPack.FileName(text)), checksum = candidate + ".sha256";
        if (File.Exists(checksum) && new FileInfo(checksum).Length == 64 &&
            Verified(candidate, await File.ReadAllTextAsync(checksum, token)))
        {
            AddVerified(text, candidate);
            return 0;
        }
        return await api!.CountAsync(client!, text, token);
    }

    internal async Task<string> AcquireAsync(string text, CancellationToken token)
    {
        if (clips.TryGetValue(text, out var existing)) return existing;
        token.ThrowIfCancellationRequested();
        string path;
        if (local is not null) path = local.Generate(text).Path;
        else
        {
            path = Path.Combine(cache, SpeechPack.FileName(text));
            byte[] pcm = await api!.GenerateAsync(client!, text, token);
            PcmAudio.Write(path, pcm);
            string checksum = path + ".sha256", temp = checksum + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, HashFile(path));
                File.Move(temp, checksum, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        AddVerified(text, path);
        return path;
    }

    internal static string HashFile(string path) => SpeechPackFormat.HashFile(path);

    internal static bool Verified(string path, string sha)
    {
        if (!File.Exists(path)) return false;
        try { SpeechPackFormat.VerifyWave(path, sha); return true; } catch (InvalidDataException) { return false; }
    }

    public void Dispose() { client?.Dispose(); local?.Dispose(); }
}
