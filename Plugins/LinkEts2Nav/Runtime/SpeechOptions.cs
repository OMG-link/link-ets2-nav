using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Runtime;

/// <summary>Local speech resources and overrides resolved relative to the host directory.</summary>
public sealed class SpeechOptions
{
    private const string BundledModel = "Assets/LinkEts2Nav/SpeechModel";
    internal const string DefaultWorker = "Tools/LinkEts2Nav.Speech/LinkEts2Nav.Speech.dll";

    public bool Enabled { get; init; }
    public string? ModelDirectory { get; init; }
    public string WorkerPath { get; init; } = DefaultWorker;
    public string CacheDirectory { get; init; } = "Cache/LinkEts2Nav/Speech";
    public string PackDirectory { get; init; } = "Assets/LinkEts2Nav/SpeechPack";
    public string Voice { get; init; } = "zf_067";
    public int Threads { get; init; } = 2;

    public static SpeechOptions Load(string hostDirectory)
    {
        string path = Path.Combine(hostDirectory, "Settings", "LinkEts2Nav.Speech.json");
        var value = new SpeechOptions();
        bool explicitEnabled = false, explicitWorker = false;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Speech configuration exceeds 64 KiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            value = document.RootElement.Deserialize<SpeechOptions>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("Speech configuration is empty.");
            explicitEnabled = document.RootElement.TryGetProperty("enabled", out _);
            explicitWorker = document.RootElement.TryGetProperty("workerPath", out _);
            if (explicitEnabled && !value.Enabled) return value;
        }

        string modelDirectory = value.ModelDirectory ?? BundledModel;
        if (string.IsNullOrWhiteSpace(modelDirectory) || string.IsNullOrWhiteSpace(value.WorkerPath)
            || string.IsNullOrWhiteSpace(value.PackDirectory) || string.IsNullOrWhiteSpace(value.CacheDirectory) || value.Voice != "zf_067" || value.Threads is < 1 or > 8)
            throw new InvalidDataException("Speech requires modelDirectory, workerPath, cacheDirectory, zf_067 and 1..8 threads.");

        modelDirectory = Path.GetFullPath(modelDirectory, hostDirectory);
        string workerPath = Path.GetFullPath(value.WorkerPath, hostDirectory);
        string workerExecutable = Path.ChangeExtension(workerPath, ".exe");
        if (!explicitWorker && File.Exists(workerExecutable)) workerPath = workerExecutable;
        string packDirectory = Path.GetFullPath(value.PackDirectory, hostDirectory);
        if (!explicitEnabled)
        {
            bool hasSpeech = File.Exists(Path.Combine(modelDirectory, "model.onnx"))
                || File.Exists(Path.Combine(packDirectory, "manifest.json"))
                || File.Exists(Path.Combine(packDirectory, "CityNames", "manifest.json"));
            if (!hasSpeech || !File.Exists(workerPath)) return value;
        }

        return new()
        {
            Enabled = true, ModelDirectory = modelDirectory,
            WorkerPath = workerPath,
            CacheDirectory = Path.GetFullPath(value.CacheDirectory, hostDirectory),
            PackDirectory = packDirectory,
            Voice = value.Voice, Threads = value.Threads
        };
    }
}
