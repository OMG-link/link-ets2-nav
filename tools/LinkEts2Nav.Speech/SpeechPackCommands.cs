using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Speech;

public sealed record SpeechJob(string Text, string File);
public sealed record SpeechJobs(int Schema, string Kind, SpeechJob[] Items);

/// <summary>Provider-neutral exchange: export text/file jobs, then import canonical WAVs.</summary>
public static class SpeechPackCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Missing pack command.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] allowed = args[0] switch
        {
            "--export-jobs" => ["--output", "--texts", "--kind"],
            "--import-pack" => ["--jobs", "--input", "--output", "--model", "--voice"],
            _ => throw new ArgumentException("Unknown pack command.")
        };
        for (int i = 1; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !allowed.Contains(args[i], StringComparer.Ordinal)
                || !options.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("Unknown, repeated or incomplete pack option; use --help.");
        string Required(string key) => options.TryGetValue(key, out var value) ? value
            : throw new ArgumentException("Missing " + key);
        string output = Path.GetFullPath(Required("--output"));
        if (args[0] == "--export-jobs")
        {
            string kind = options.GetValueOrDefault("--kind", "fixed");
            if (kind == "city-names" && !options.ContainsKey("--texts"))
                throw new ArgumentException("City-name jobs require --texts UTF8_FILE.");
            string[] texts;
            if (options.TryGetValue("--texts", out var list))
            {
                using var reader = new StreamReader(list, Encoding.UTF8, true);
                texts = SpeechTextCatalog.Read(reader);
            }
            else texts = SpeechTextCatalog.DefaultTexts.ToArray();
            var jobs = new SpeechJobs(1, kind, texts.Select(t => new SpeechJob(t, SpeechPack.FileName(t))).ToArray());
            Validate(jobs);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("Speech jobs exported: " + jobs.Items.Length + ".");
            return 0;
        }

        string jobFile = Required("--jobs"), input = Path.GetFullPath(Required("--input"));
        if (new FileInfo(jobFile).Length > 2097152) throw new InvalidDataException("Speech jobs file is too large.");
        var imported = JsonSerializer.Deserialize<SpeechJobs>(await File.ReadAllTextAsync(jobFile))
            ?? throw new InvalidDataException("Empty speech jobs.");
        Validate(imported);
        string model = options.GetValueOrDefault("--model", "external"), voice = options.GetValueOrDefault("--voice", "default");
        if (model.Length > 128 || voice.Length > 128 || model.Any(char.IsControl) || voice.Any(char.IsControl))
            throw new InvalidDataException("Invalid model/voice metadata.");
        var projected = new PackManifest(1, model, voice, imported.Items.Select(j =>
            new PackEntry(j.Text, SpeechPack.FileName(j.Text), new string('0', 64))).ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(projected, new JsonSerializerOptions { WriteIndented = true }).Length
            > (imported.Kind == "fixed" ? 262144 : 2097152))
            throw new InvalidDataException("Imported manifest exceeds the runtime pack size limit.");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        string root = Path.TrimEndingDirectorySeparator(input) + Path.DirectorySeparatorChar;
        string outputRoot = Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.StartsWith(outputRoot, comparison) || outputRoot.StartsWith(root, comparison))
            throw new InvalidDataException("Input and output directories must be separate and not nested.");
        foreach (var job in imported.Items)
        {
            if (Path.IsPathRooted(job.File)) throw new InvalidDataException("Job filenames must be relative to --input.");
            string path = Path.GetFullPath(job.File, input);
            if (!path.StartsWith(root, comparison))
                throw new InvalidDataException("Job filename leaves the input directory.");
            // Validate all expected inputs before the builder can alter the output.
            // Extra files are deliberately ignored; the job list alone defines this pack.
            PcmAudio.Read(path);
            sources.Add(job.Text, path);
        }
        await SpeechPackBuilder.BuildAsync(output, model, voice, imported.Items.Select(j => j.Text),
            (text, _) => Task.FromResult(PcmAudio.Read(sources[text])), Console.WriteLine);
        Console.WriteLine("Speech pack imported: " + imported.Items.Length + " entries.");
        return 0;
    }

    private static void Validate(SpeechJobs jobs)
    {
        if (jobs.Schema != 1 || jobs.Kind is not ("fixed" or "city-names") || jobs.Items is null
            || jobs.Items.Length < 1 || jobs.Items.Length > (jobs.Kind == "fixed" ? 256 : 4096))
            throw new InvalidDataException("Invalid speech jobs schema, kind or count.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs.Items)
            if (job is null || string.IsNullOrWhiteSpace(job.Text) || job.Text != job.Text.Trim().Normalize(NormalizationForm.FormC)
                || job.Text.Length > (jobs.Kind == "fixed" ? 2048 : 128) || job.Text.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(job.File) || !seen.Add(job.Text))
                throw new InvalidDataException("Invalid or duplicate speech job.");
    }
}
