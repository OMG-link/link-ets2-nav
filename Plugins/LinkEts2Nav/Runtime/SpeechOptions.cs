using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Runtime;

/// <summary>Optional local synthesis worker; paths resolve against the host, never its working directory.</summary>
public sealed class SpeechOptions
{
    public bool Enabled { get; init; }
    public string? ModelDirectory { get; init; }
    public string WorkerPath { get; init; } = "Tools/LinkEts2Nav.Speech/LinkEts2Nav.Speech.dll";
    public string CacheDirectory { get; init; } = "Cache/LinkEts2Nav/Speech";
    public string PackDirectory { get; init; } = "Assets/LinkEts2Nav/SpeechPack";
    public string Voice { get; init; } = "zf_067";
    public int Threads { get; init; } = 2;

    public static SpeechOptions Load(string hostDirectory)
    {
        string path = Path.Combine(hostDirectory, "Settings", "LinkEts2Nav.Speech.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Speech configuration exceeds 64 KiB.");
        var value = JsonSerializer.Deserialize<SpeechOptions>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Speech configuration is empty.");
        if (!value.Enabled) return value;
        if (string.IsNullOrWhiteSpace(value.ModelDirectory) || string.IsNullOrWhiteSpace(value.WorkerPath)
            || string.IsNullOrWhiteSpace(value.PackDirectory) || string.IsNullOrWhiteSpace(value.CacheDirectory) || value.Voice != "zf_067" || value.Threads is < 1 or > 8)
            throw new InvalidDataException("Speech requires modelDirectory, workerPath, cacheDirectory, zf_067 and 1..8 threads.");
        return new()
        {
            Enabled = true, ModelDirectory = Path.GetFullPath(value.ModelDirectory, hostDirectory),
            WorkerPath = Path.GetFullPath(value.WorkerPath, hostDirectory),
            CacheDirectory = Path.GetFullPath(value.CacheDirectory, hostDirectory),
            PackDirectory = Path.GetFullPath(value.PackDirectory, hostDirectory),
            Voice = value.Voice, Threads = value.Threads
        };
    }
}
