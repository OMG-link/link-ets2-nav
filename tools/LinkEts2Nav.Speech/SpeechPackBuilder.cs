using System.Text.Json;
namespace LinkEts2Nav.Speech;

/// <summary>Resumable generation preserves cached entries beyond the current checkpoint.</summary>
public static class SpeechPackBuilder
{
    public static async Task<PackManifest> BuildAsync(string output, string model, string voice,
        IEnumerable<string> texts, Func<string, CancellationToken, Task<byte[]>> generate,
        Action<string>? log = null, CancellationToken cancellationToken = default, bool reuseCachedAudio = true,
        Action<int>? completedCount = null)
    {
        Directory.CreateDirectory(output);
        string manifestPath = Path.Combine(output, "manifest.json");
        PackManifest? previous = null;
        if (File.Exists(manifestPath))
        {
            // A checkpoint can contain stale or incomplete entries: retain verified
            // recordings individually and regenerate only what is missing or damaged.
            using var stream = File.OpenRead(manifestPath);
            previous = await JsonSerializer.DeserializeAsync<PackManifest>(stream, cancellationToken: cancellationToken);
        }
        string[] planned = texts.Distinct(StringComparer.Ordinal).ToArray();
        var plannedTexts = planned.ToHashSet(StringComparer.Ordinal);
        var checkpoint = reuseCachedAudio && previous is { Schema: 1 } && previous.Model == model && previous.Voice == voice
            ? (previous.Entries ?? []).Where(e => e is not null && !e.ExactOnly && !e.Composed && plannedTexts.Contains(e.Text)
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
                    SpeechPackFormat.ValidateEntry(output, cached);
                    valid = true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
            if (valid)
            {
                completed.Add(cached!);
                completedCount?.Invoke(completed.Count);
                log?.Invoke("Cached: " + text);
                continue;
            }
            log?.Invoke("Generating: " + text);
            byte[] pcm = await generate(text, cancellationToken);
            PcmAudio.Write(path, pcm);
            var entry = new PackEntry(text, file, SpeechPackFormat.HashFile(path));
            completed.Add(entry);
            checkpoint[text] = entry;
            // Keep later cached entries as well, so an interrupted mixed cached/new
            // run does not forget their provenance and bill them again on resume.
            await SaveAsync(new(1, model, voice, checkpoint.Values.ToArray()));
            completedCount?.Invoke(completed.Count);
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
