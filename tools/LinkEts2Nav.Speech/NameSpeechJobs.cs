using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Speech;

/// <summary>Combines reviewed name lists and exports only missing or damaged name clips.</summary>
public static class NameSpeechJobs
{
    public static async Task<int> RunAsync(string[] args)
    {
        var lists = new List<string>();
        string? output = null, packDirectory = null;
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Incomplete name-job option.");
            switch (args[i])
            {
                case "--texts": lists.Add(args[i + 1]); break;
                case "--output" when output is null: output = args[i + 1]; break;
                case "--pack" when packDirectory is null: packDirectory = args[i + 1]; break;
                default: throw new ArgumentException("Unknown or repeated name-job option.");
            }
        }
        if (lists.Count == 0 || output is null)
            throw new ArgumentException("Use --prepare-name-jobs --texts FILE [--texts FILE] --output FILE [--pack CityNames_DIR].");
        var sources = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string file in lists)
        {
            foreach (string line in await File.ReadAllLinesAsync(file, Encoding.UTF8))
            {
                string text = line.Trim().Normalize(NormalizationForm.FormC);
                if (text.Length == 0) continue;
                if (text.Length > 128 || text.Any(char.IsControl)) throw new InvalidDataException("Invalid place name.");
                if (!sources.TryGetValue(text, out var names)) sources[text] = names = new(StringComparer.Ordinal);
                names.Add(Path.GetFileName(file));
            }
        }
        if (sources.Count is < 1 or > 4096) throw new InvalidDataException("A name pack supports 1..4096 distinct names.");
        var entries = new Dictionary<string, PackEntry>(StringComparer.Ordinal);
        if (packDirectory is not null && File.Exists(Path.Combine(packDirectory, "manifest.json")))
        {
            string path = Path.Combine(packDirectory, "manifest.json");
            if (new FileInfo(path).Length > 2097152) throw new InvalidDataException("Place-name manifest too large.");
            var manifest = JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(path));
            if (manifest is not { Schema: 1, Entries: not null } || manifest.Entries.Length > 4096)
                throw new InvalidDataException("Invalid place-name manifest.");
            foreach (var entry in manifest.Entries)
                if (entry is null || string.IsNullOrWhiteSpace(entry.Text) || !entries.TryAdd(entry.Text, entry))
                    throw new InvalidDataException("Invalid or duplicate place-name manifest entry.");
            if (entries.Keys.Union(sources.Keys, StringComparer.Ordinal).Count() > 4096)
                throw new InvalidDataException("The extended name pack would exceed 4096 entries.");
        }
        var coverage = sources.Select(p =>
        {
            string state = "missing";
            if (entries.TryGetValue(p.Key, out var entry))
            {
                state = "invalid";
                try
                {
                    if (entry.File == SpeechPack.FileName(p.Key))
                    {
                        string path = Path.Combine(packDirectory!, entry.File);
                        PcmAudio.Read(path);
                        if (SpeechPack.Hash(File.ReadAllBytes(path)) == entry.Sha256) state = "ready";
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
            return new { Text = p.Key, Sources = p.Value.ToArray(), State = state };
        }).ToArray();
        var jobs = new SpeechJobs(1, "city-names", coverage.Where(p => p.State != "ready")
            .Select(p => new SpeechJob(p.Text, SpeechPack.FileName(p.Text))).ToArray());
        output = Path.GetFullPath(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var inputs = lists.Select(Path.GetFullPath).ToArray();
        if (inputs.Contains(output, comparison) || inputs.Contains(output + ".coverage.json", comparison)
            || packDirectory is not null && comparison.Equals(output, Path.GetFullPath(Path.Combine(packDirectory, "manifest.json"))))
            throw new ArgumentException("Output must not overwrite an input list.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(jobs, options), new UTF8Encoding(false));
        await File.WriteAllTextAsync(output + ".coverage.json", JsonSerializer.Serialize(coverage, options), new UTF8Encoding(false));
        Console.WriteLine($"Names: {sources.Count}; ready: {coverage.Count(p => p.State == "ready")}; to generate: {jobs.Items.Length}.");
        return 0;
    }
}
