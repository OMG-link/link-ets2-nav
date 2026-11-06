using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Runtime;

/// <summary>Local audio selection; custom media is never bundled with the plugin.</summary>
public sealed class WarningAudioOptions
{
    public string? FilePath { get; init; }

    public static string? LoadFilePath(string hostDirectory)
    {
        string config = Path.Combine(hostDirectory, "Settings", "LinkEts2Nav.Audio.json");
        if (!File.Exists(config)) return null;
        if (new FileInfo(config).Length > 65536) throw new InvalidDataException("Audio configuration exceeds 64 KiB.");
        var options = JsonSerializer.Deserialize<WarningAudioOptions>(File.ReadAllText(config),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidDataException("Audio configuration is empty.");
        if (string.IsNullOrWhiteSpace(options.FilePath)) return null;
        string path = Path.GetFullPath(options.FilePath, hostDirectory);
        if (!new[] { ".wav", ".ogg", ".mp3", ".flac" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Custom warning audio must be WAV, OGG, MP3 or FLAC; decode FMOD banks first.");
        return path;
    }
}
