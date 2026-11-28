using LinkEts2Nav.Runtime.SpeechFiles;

namespace LinkEts2Nav.Runtime;

/// <summary>Verify pre-generated audio and retain only file paths for playback.</summary>
public static class PreGeneratedSpeech
{
    public static IReadOnlyDictionary<string, string> Load(string directory, Action<string>? log = null, int? maximumEntries = null,
        CancellationToken cancellationToken = default) => Load(directory, log, maximumEntries, null, cancellationToken);

    internal static IReadOnlyDictionary<string, string> LoadNames(string directory, Action<string>? log = null,
        CancellationToken cancellationToken = default) =>
        Load(directory, log, SpeechPackFormat.MaximumNameEntries, SpeechPackFormat.MaximumNameManifestBytes, cancellationToken);

    private static IReadOnlyDictionary<string, string> Load(string directory, Action<string>? log, int? maximumEntries,
        int? maximumBytes, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string manifest = Path.Combine(directory, "manifest.json");
        FileAttributes attributes;
        try { attributes=File.GetAttributes(manifest); }
        catch(FileNotFoundException) { return result; }
        catch(DirectoryNotFoundException) { return result; }
        if((attributes&FileAttributes.Directory)!=0)throw new InvalidDataException("Speech pack manifest must be a file.");
        var pack = SpeechPackFormat.ReadManifest(manifest, maximumEntries, maximumBytes, cancellationToken);
        foreach (var entry in pack.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpeechPackFormat.ValidateEntry(directory, entry);
            result.Add(entry.Text, Path.Combine(directory, entry.File));
        }
        log?.Invoke("预生成语音已预载：" + result.Count + " 条，首次触发可直接播放。");
        return result;
    }
}
