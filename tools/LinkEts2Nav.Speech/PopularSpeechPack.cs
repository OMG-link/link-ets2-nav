using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Speech;

internal static class PopularSpeechPack
{
    // Read only the published speech-usage fields needed by this standalone executable.
    private sealed record Usage([property: JsonRequired] int Schema, [property: JsonRequired] int MetricVersion,
        [property: JsonRequired] Entry[] Entries);
    private sealed record Entry([property: JsonRequired] string Text, [property: JsonRequired] long PlayedCount,
        [property: JsonRequired] double PreparationMilliseconds);
    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public static async Task<int> GenerateAsync(string usagePath, string topValue, string configPath, string outputPath)
    {
        if (!int.TryParse(topValue, NumberStyles.None, CultureInfo.InvariantCulture, out int top) || top is < 1 or > 256)
            throw new ArgumentException("--top must be between 1 and 256.");
        var jobs = await SelectAsync(usagePath, top);
        var api = await SpeechApi.ReadAsync(configPath);
        var profile = api.Profile;
        SpeechPackCommands.Validate(jobs);
        SpeechPackCommands.ValidateManifest(jobs, api.Model, api.Voice, profile);

        var output = new DirectoryInfo(outputPath);
        if (output.Parent is null) throw new ArgumentException("Choose a dedicated speech pack directory.");
        output.Parent.Create();
        string sibling = Path.Combine(output.Parent.FullName, "." + output.Name + ".popular-");
        using var exclusive = new FileStream(sibling + "writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var stage = new DirectoryInfo(sibling + Guid.NewGuid().ToString("N"));
        stage.Create();
        try
        {
            bool reuse = PrepareStage(output, stage, jobs, profile);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { MaxResponseContentBufferSize = PcmAudio.MaximumWaveBytes };
            var built = await SpeechPackBuilder.BuildAsync(stage.FullName, api.Model, api.Voice, jobs.Items.Select(j => j.Text),
                (text, token) => api.GenerateAsync(client, text, token), Console.WriteLine, reuseCachedAudio: reuse);
            await File.WriteAllTextAsync(Path.Combine(stage.FullName, "manifest.json"),
                JsonSerializer.Serialize(built with { ApiProfile = profile }, ManifestJson));
            Publish(stage.FullName, output.FullName, sibling + "previous-" + Guid.NewGuid().ToString("N"));
            Console.WriteLine("Popular speech pack ready: " + jobs.Items.Length + " entries.");
            return 0;
        }
        finally { CleanUp(stage.FullName); }
    }

    private static async Task<SpeechJobs> SelectAsync(string path, int top)
    {
        // The running navigator replaces the summary by rename at a checkpoint.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var usage = await JsonSerializer.DeserializeAsync<Usage>(input);
        if (usage is not { Schema: 1, MetricVersion: 1, Entries: not null })
            throw new InvalidDataException("Unsupported speech usage summary schema or metric version.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in usage.Entries)
            if (e is null || string.IsNullOrWhiteSpace(e.Text) || !seen.Add(e.Text) || e.PlayedCount < 0
                || !double.IsFinite(e.PreparationMilliseconds) || e.PreparationMilliseconds < 0)
                throw new InvalidDataException("Invalid or duplicate speech usage entry.");
        var items = usage.Entries.Where(e => e.PlayedCount > 0).OrderByDescending(e => e.PlayedCount)
            .ThenByDescending(e => e.PreparationMilliseconds).ThenBy(e => e.Text, StringComparer.Ordinal).Take(top)
            .Select(e => new SpeechJob(e.Text, SpeechPack.FileName(e.Text))).ToArray();
        if (items.Length == 0) throw new InvalidDataException("The usage summary contains no phrases with a positive PlayedCount.");
        return new(1, "fixed", items);
    }

    private static bool PrepareStage(DirectoryInfo output, DirectoryInfo stage, SpeechJobs jobs, string profile)
    {
        if (!output.Exists) return false;
        string manifest = Path.Combine(output.FullName, "manifest.json");
        PackManifest? previous = null;
        if (File.Exists(manifest))
        {
            if (new FileInfo(manifest).Length > 262144) throw new InvalidDataException("Existing speech pack manifest is too large.");
            previous = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifest));
            if (previous is not { Schema: 1, Entries: not null } || previous.Entries.Length > 256
                || previous.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Text) || e.File != SpeechPack.FileName(e.Text)))
                throw new InvalidDataException("Existing output does not contain a valid fixed speech pack.");
        }
        else if (output.EnumerateFileSystemInfos().Any() && !File.Exists(Path.Combine(output.FullName, "CityNames", "manifest.json")))
            throw new InvalidDataException("Use a new or empty output directory, or an existing speech pack.");

        bool reuse = previous?.ApiProfile == profile;
        var wanted = jobs.Items.Select(j => j.File).ToHashSet(StringComparer.Ordinal);
        var omit = (previous?.Entries ?? []).Where(e => !reuse || !wanted.Contains(e.File))
            .Select(e => e.File).ToHashSet(StringComparer.Ordinal);
        CopyDirectory(output, stage, omit);
        return reuse;
    }

    // Keep CityNames and other accompanying files. Never traverse a link out of the pack being replaced.
    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination, HashSet<string>? omit = null)
    {
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The speech pack contains a directory link.");
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            if (omit?.Contains(entry.Name) == true) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The speech pack contains a filesystem link.");
            string target = Path.Combine(destination.FullName, entry.Name);
            if (entry is DirectoryInfo subdirectory) CopyDirectory(subdirectory, Directory.CreateDirectory(target));
            else File.Copy(entry.FullName, target);
        }
    }

    private static void Publish(string stage, string output, string backup)
    {
        if (!Directory.Exists(output)) { Directory.Move(stage, output); return; }
        Directory.Move(output, backup);
        try { Directory.Move(stage, output); }
        catch { Directory.Move(backup, output); throw; }
        CleanUp(backup);
    }

    private static void CleanUp(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Could not remove temporary speech pack directory: " + directory); }
    }
}
