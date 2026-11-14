using System.Text.Json;
namespace LinkEts2Nav.Speech;

/// <summary>Resumable generation preserves cached entries beyond the current checkpoint.</summary>
public static class SpeechPackBuilder
{
    public static async Task<PackManifest> BuildAsync(string output, string model, string voice,
        IEnumerable<string> texts, Func<string, CancellationToken, Task<byte[]>> generate,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(output);
        string manifestPath = Path.Combine(output, "manifest.json");
        PackManifest? previous = null;
        if (File.Exists(manifestPath))
        {
            if (new FileInfo(manifestPath).Length > 2097152) throw new InvalidDataException("Speech pack manifest too large.");
            previous = JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        }
        string[] planned = texts.Distinct(StringComparer.Ordinal).ToArray();
        var checkpoint = previous is { Schema: 1 } && previous.Model == model && previous.Voice == voice
            ? (previous.Entries ?? []).Where(e => e is not null && planned.Contains(e.Text, StringComparer.Ordinal)
                && e.File == SpeechPack.FileName(e.Text)).ToDictionary(e => e.Text, StringComparer.Ordinal)
            : new Dictionary<string, PackEntry>(StringComparer.Ordinal);
        var completed = new List<PackEntry>();
        foreach (string text in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = SpeechPack.FileName(text), path = Path.Combine(output, file);
            checkpoint.TryGetValue(text, out var cached);
            bool valid = false;
            if (cached is not null && File.Exists(path))
            {
                try
                {
                    PcmAudio.Read(path);
                    valid = SpeechPack.Hash(await File.ReadAllBytesAsync(path, cancellationToken)) == cached.Sha256;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
            if (valid)
            {
                completed.Add(cached!);
                log?.Invoke("Cached: " + text);
                continue;
            }
            log?.Invoke("Generating: " + text);
            byte[] pcm = await generate(text, cancellationToken);
            PcmAudio.Write(path, pcm);
            var entry = new PackEntry(text, file, SpeechPack.Hash(await File.ReadAllBytesAsync(path, cancellationToken)));
            completed.Add(entry);
            checkpoint[text] = entry;
            // Keep later cached entries as well, so an interrupted mixed cached/new
            // run does not forget their provenance and bill them again on resume.
            await SaveAsync(new(1, model, voice, checkpoint.Values.ToArray()));
        }
        var result = new PackManifest(1, model, voice, completed.ToArray());
        // Also save after all-cache runs and cached tails following the last new clip.
        await SaveAsync(result);
        return result;

        async Task SaveAsync(PackManifest manifest)
        {
            string temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest,
                    new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                File.Move(temporary, manifestPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
