using System.Text.Json;
using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Speech;

internal sealed class PackGenerationException(string code) : Exception { public string Code { get; } = code; }

internal static class PopularSpeechPack
{
    public static async Task<int> RunWorkerAsync()
    {
        using var stop = new CancellationTokenSource();
        static void Send(GenerationReply reply) { Console.WriteLine(JsonSerializer.Serialize(reply)); Console.Out.Flush(); }
        var status = new GenerationReply("progress");
        try
        {
            var line = await Console.In.ReadLineAsync();
            var job = JsonSerializer.Deserialize<GenerationJob>(line ?? "") ?? throw new PackGenerationException("request");
            // The parent holds stdin open for this job. EOF also stops requests if the host exits.
            _ = Task.Run(() => { try { Console.In.ReadLine(); } finally { try { stop.Cancel(); } catch (ObjectDisposedException) { } } });
            status = status with { Budget = job.TokenBudget };
            await GenerateAsync(job, update => { status = update; Send(update); }, stop.Token);
            Send(status with { Kind = "done" });
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string code = e switch {
                OperationCanceledException => stop.IsCancellationRequested ? "cancelled" : "timeout",
                UnavailableModelResource => "local-model",
                PackGenerationException error => error.Code,
                HttpRequestException => "http",
                JsonException => "template",
                InvalidDataException => "pack",
                UnauthorizedAccessException or IOException => "storage",
                _ => "worker"
            };
            int? http = e is HttpRequestException { StatusCode: { } httpStatus } ? (int)httpStatus : null;
            Send(status with { Kind = "error", Code = code, HttpStatus = http });
            return 2;
        }
    }

    private static async Task GenerateAsync(GenerationJob job, Action<GenerationReply> progress, CancellationToken token)
    {
        if (job.TokenBudget < 1) throw new PackGenerationException("budget");
        if (job.Candidates is null || job.Candidates.Any(c => c is null || c.PlayedCount < 1 ||
            !double.IsFinite(c.PreparationMilliseconds) || c.PreparationMilliseconds < 0 || string.IsNullOrWhiteSpace(c.Text)))
            throw new PackGenerationException("request");
        var jobs = new SpeechJobs(1, "fixed", job.Candidates.Select(c => new SpeechJob(c.Text, SpeechPack.FileName(c.Text))).ToArray());
        SpeechPackCommands.Validate(jobs);
        SpeechPackCommands.ValidateManifest(jobs, job.Model, job.Voice);
        using var audio = new BudgetSpeechSource(job);
        var output = new DirectoryInfo(job.OutputDirectory);
        output.Parent!.Create();
        string sibling = Path.Combine(output.Parent.FullName, "." + output.Name + ".popular-");
        using var exclusive = new FileStream(sibling + "writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var stage = Directory.CreateDirectory(sibling + Guid.NewGuid().ToString("N"));
        try
        {
            var previous = ReadManifest(job.SourcePack);
            var old = ReadManifest(output.FullName);
            if (output.Exists && output.EnumerateFileSystemInfos().Any())
            {
                if (old?.ApiProfile is null) throw new PackGenerationException("occupied");
                var owned = old.Entries.Select(e => e.File).Append("manifest.json").Append("CityNames")
                    .Append(JourneySpeechPack.DirectoryName).Append(NavigationSpeechPack.DirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                CopyDirectory(output, stage, token, owned);
            }
            var entries = CopySource(job.SourcePack, previous, stage, audio, token);
            await BuildRecordings(job, audio, stage, entries, progress, token);
            var result = new PackManifest(1, job.Model, job.Voice, entries.Values.ToArray()) { ApiProfile = audio.Profile };
            await using (var file = File.Create(Path.Combine(stage.FullName, "manifest.json")))
                await JsonSerializer.SerializeAsync(file, result, new JsonSerializerOptions { WriteIndented = true }, token);
            token.ThrowIfCancellationRequested();
            if (!output.Exists) Directory.Move(stage.FullName, output.FullName);
            else
            {
                string backup = sibling + "previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(output.FullName, backup);
                try { Directory.Move(stage.FullName, output.FullName); }
                catch { Directory.Move(backup, output.FullName); throw; }
                CleanUp(backup);
            }
        }
        finally { CleanUp(stage.FullName); }
    }

    private static Dictionary<string, PackEntry> CopySource(string directory, PackManifest? pack, DirectoryInfo stage,
        BudgetSpeechSource audio, CancellationToken token)
    {
        var entries = new Dictionary<string, PackEntry>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return entries;
        var source = new DirectoryInfo(directory);
        string relative = Path.GetRelativePath(source.FullName, stage.FullName);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PackGenerationException("source");
        var owned = (pack?.Entries ?? []).Select(e => e.File).Append("manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        CopyDirectory(source, stage, token, owned);
        foreach (var entry in pack?.Entries ?? [])
        {
            token.ThrowIfCancellationRequested();
            string sourceFile = Path.Combine(source.FullName, entry.File);
            if (!File.Exists(sourceFile) || File.GetAttributes(sourceFile).HasFlag(FileAttributes.ReparsePoint) ||
                !BudgetSpeechSource.Verified(sourceFile, entry.Sha256)) throw new PackGenerationException("source");
            string target = Path.Combine(stage.FullName, entry.File);
            File.Copy(sourceFile, target, true);
            string origin = entry.VoiceProfile ?? pack!.ApiProfile ?? "";
            entries.Add(entry.Text, entry with { VoiceProfile = origin == audio.Profile ? null : origin });
            if (!entry.Composed && origin == audio.Profile) audio.AddVerified(entry.Text, target);
        }
        return entries;
    }

    private static async Task BuildRecordings(GenerationJob job, BudgetSpeechSource audio, DirectoryInfo stage,
        Dictionary<string, PackEntry> entries, Action<GenerationReply> progress, CancellationToken token)
    {
        var rows = job.Candidates.Select(BudgetPhrase.From).ToArray();
        var retained = entries.ToDictionary(pair => pair.Key, pair => pair.Value.Composed, StringComparer.Ordinal);
        // Imported recordings have an explicitly unknown origin. An earlier generated voice may
        // be upgraded after covering new texts, while a hand-picked original is always retained.
        var replaceVoice = entries.Values.Where(e => e.VoiceProfile is { Length: > 0 }).Select(e => e.Text).ToHashSet(StringComparer.Ordinal);
        var costs = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            bool exists = retained.TryGetValue(row.Text, out bool composed);
            if (exists && !composed && !replaceVoice.Contains(row.Text)) continue;
            token.ThrowIfCancellationRequested();
            if (!costs.ContainsKey(row.Text)) costs.Add(row.Text, await audio.CostAsync(row.Text, token));
            // A free exact recording dominates every alternative for this phrase. Its clauses
            // will still be checked if a different, uncovered phrase actually needs them.
            if (exists || costs[row.Text] == 0) { rows[i] = row with { Parts = [] }; continue; }
            foreach (string input in row.Parts)
            {
                token.ThrowIfCancellationRequested();
                if (!costs.ContainsKey(input)) costs[input] = await audio.CostAsync(input, token);
            }
        }
        var recordings = SpeechBudgetPlanner.Select(rows, retained, replaceVoice, costs, job.TokenBudget, token);
        if (recordings.Length == 0) throw new PackGenerationException("budget");
        var status = new GenerationReply("progress", Total: recordings.Length, Budget: job.TokenBudget);
        progress(status);
        foreach (var recording in recordings)
        {
            token.ThrowIfCancellationRequested();
            if (recording.Inputs.Length > 0)
            {
                string filename = SpeechPack.FileName(recording.Text), target = Path.Combine(stage.FullName, filename);
                using var joined = new MemoryStream();
                foreach (string input in recording.Inputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (!audio.HasClip(input))
                    {
                        long cost = costs[input];
                        if (cost > job.TokenBudget - status.SpentTokens) throw new PackGenerationException("budget");
                        status = status with { SpentTokens = status.SpentTokens + cost };
                        progress(status);
                    }
                    string path = await audio.AcquireAsync(input, token);
                    if (!recording.Composed) File.Copy(path, target, true);
                    else
                    {
                        byte[] samples = PcmAudio.Read(path);
                        if (samples.Length > PcmAudio.MaximumWaveBytes - 44 - joined.Length) throw new PackGenerationException("wav");
                        joined.Write(samples);
                    }
                }
                if (recording.Composed) PcmAudio.Write(target, joined.ToArray());
                entries[recording.Text] = new(recording.Text, filename, BudgetSpeechSource.HashFile(target)) { Composed = recording.Composed };
            }
            status = status with { Completed = status.Completed + 1, WholeCount = status.WholeCount + (recording.Composed ? 0 : 1),
                ComposedCount = status.ComposedCount + (recording.Composed ? 1 : 0) };
            progress(status);
        }
    }

    private static PackManifest? ReadManifest(string directory)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            var pack = SpeechPackFormat.ReadManifest(path);
            foreach (var entry in pack.Entries)
            {
                SpeechPackFormat.EntryPath(directory, entry);
                if (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(char.IsAsciiHexDigit))
                    throw new InvalidDataException("Invalid speech pack checksum.");
            }
            return pack;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new PackGenerationException("source"); }
    }

    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo destination, CancellationToken token, HashSet<string>? omit = null)
    {
        if (source.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new PackGenerationException("link");
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if (omit?.Contains(entry.Name) == true) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new PackGenerationException("link");
            if (entry is DirectoryInfo subdirectory) CopyDirectory(subdirectory, destination.CreateSubdirectory(entry.Name), token);
            else File.Copy(entry.FullName, Path.Combine(destination.FullName, entry.Name), true);
        }
    }

    private static void CleanUp(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Console.Error.WriteLine("Temporary generation directory could not be removed."); }
    }
}
