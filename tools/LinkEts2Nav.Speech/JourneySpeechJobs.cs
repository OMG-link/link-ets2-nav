using System.Text;
using System.Text.Json;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

internal static class JourneySpeechJobs
{
    private static IEnumerable<string> Texts(string? file)
    {
        yield return JourneySpeechText.Start;
        yield return JourneySpeechText.Replan;
        yield return JourneySpeechText.DistancePrefix;
        yield return JourneySpeechText.Meter;
        yield return JourneySpeechText.Kilometer;
        yield return JourneySpeechText.TimePrefix;
        yield return JourneySpeechText.TimeSuffix;
        foreach (char digit in JourneySpeechText.NumberSounds) yield return digit.ToString();
        // Keep short trips fluent. Everything outside these common clauses is already covered
        // by the same recorded numbers and units; historical clauses can be added independently.
        for (int index = 1; index <= 10; index++)
        {
            string n = JourneySpeechText.FormattedNumber(index);
            yield return JourneySpeechText.TimePrefix + (index == 2 ? "两" : n) + JourneySpeechText.TimeSuffix;
            yield return JourneySpeechText.DistancePrefix + JourneySpeechText.FormattedNumber(index * 100) + JourneySpeechText.Meter;
            yield return JourneySpeechText.DistancePrefix + (index == 2 ? "两" : n) + JourneySpeechText.Kilometer;
            if (index < 10) yield return JourneySpeechText.DistancePrefix + "一点" + n + JourneySpeechText.Kilometer;
        }
        if (file is null) yield break;
        foreach (string line in File.ReadLines(file, Encoding.UTF8))
        {
            string text = line.Trim();
            if (text.Length == 0) continue;
            var journey = JourneySpeechText.Parse(text) ?? throw new InvalidDataException("Expected a complete journey announcement in --texts.");
            yield return journey.DistanceClause;
            yield return journey.TimeClause;
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        bool export = args[0] == "--export-journey-jobs";
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], args[i + 1]) ||
                (args[i] is not ("--output" or "--texts") && (export || args[i] is not ("--model-dir" or "--cache-dir" or "--voice" or "--threads"))))
                throw new ArgumentException("Invalid journey pack option; use --help.");
        }
        if (!options.Remove("--output", out string? output)) throw new ArgumentException("Journey jobs require --output.");
        options.Remove("--texts", out var file);
        output = Path.GetFullPath(output);
        var texts = Texts(file).Distinct(StringComparer.Ordinal).ToArray();
        if (export)
        {
            var jobs = new SpeechJobs(1, "fixed", texts.Select(t => new SpeechJob(t, SpeechPack.FileName(t))).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Journey recording jobs: " + texts.Length);
            return 0;
        }

        var settings = WorkerOptions.Parse(options.SelectMany(pair => new[] { pair.Key, pair.Value }).ToArray());
        using var synthesis = new KokoroSynthesis(settings);
        string model = "kokoro-" + synthesis.Fingerprint, manifest = Path.Combine(output, "manifest.json");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            using var stream = File.OpenRead(manifest);
            var previous = JsonSerializer.Deserialize<PackManifest>(stream);
            if (previous is not { Schema: 1, Entries: not null } || previous.Model != model || previous.Voice != settings.Voice ||
                previous.Entries.Any(e => e is null || e.Composed || e.VoiceProfile is not null))
                throw new InvalidDataException("Generate a different journey voice in a separate empty directory.");
            texts = previous.Entries.Select(e => e.Text).Concat(texts).Distinct(StringComparer.Ordinal).ToArray();
        }
        await SpeechPackBuilder.BuildAsync(output, model, settings.Voice, texts,
            (text, _) => Task.FromResult(PcmAudio.Read(synthesis.Generate(text).Path)), Console.WriteLine);
        Console.WriteLine("Journey recordings ready: " + texts.Length);
        return 0;
    }
}
