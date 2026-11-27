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
        try
        {
            var line = await Console.In.ReadLineAsync();
            var job = JsonSerializer.Deserialize<GenerationJob>(line ?? "") ?? throw new PackGenerationException("request");
            // The parent holds stdin open for this job. EOF also stops requests if the host exits.
            _ = Task.Run(() => { try { Console.In.ReadLine(); } finally { try { stop.Cancel(); } catch (ObjectDisposedException) { } } });
            await GenerateAsync(job, count => Send(new("progress", count)), stop.Token);
            Send(new("done", job.Texts.Length));
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string code = e switch {
                OperationCanceledException => stop.IsCancellationRequested ? "cancelled" : "timeout",
                PackGenerationException error => error.Code,
                HttpRequestException => "http",
                JsonException => "template",
                InvalidDataException => "pack",
                UnauthorizedAccessException or IOException => "storage",
                _ => "worker"
            };
            int? http = e is HttpRequestException { StatusCode: { } status } ? (int)status : null;
            Send(new("error", Code: code, HttpStatus: http));
            return 2;
        }
    }

    private static async Task GenerateAsync(GenerationJob job, Action<int> progress, CancellationToken token)
    {
        var api = SpeechApi.Create(job);
        var profile = api.Profile;
        var jobs = new SpeechJobs(1, "fixed", job.Texts.Select(t => new SpeechJob(t, SpeechPack.FileName(t))).ToArray());
        SpeechPackCommands.Validate(jobs);
        SpeechPackCommands.ValidateManifest(jobs, job.Model, job.Voice, profile);
        var output = new DirectoryInfo(job.OutputDirectory);
        output.Parent!.Create();
        string sibling = Path.Combine(output.Parent.FullName, "." + output.Name + ".popular-");
        using var exclusive = new FileStream(sibling + "writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var stage = Directory.CreateDirectory(sibling + Guid.NewGuid().ToString("N"));
        try
        {
            var previous = ReadManifest(job.SourcePack);
            var source = previous?.ApiProfile == profile
                ? previous.Entries.ToDictionary(e => e.Text, StringComparer.Ordinal) : new Dictionary<string, PackEntry>();
            var old = ReadManifest(output.FullName);
            if (output.Exists && output.EnumerateFileSystemInfos().Any())
            {
                if (old?.ApiProfile is null) throw new PackGenerationException("occupied");
                var owned = old.Entries.Select(e => e.File).Append("manifest.json").Append("CityNames").ToHashSet(StringComparer.OrdinalIgnoreCase);
                CopyDirectory(output, stage, token, owned);
            }
            var names = new DirectoryInfo(Path.Combine(job.SourcePack, "CityNames"));
            if (File.Exists(Path.Combine(names.FullName, "manifest.json")))
                CopyDirectory(names, stage.CreateSubdirectory("CityNames"), token);

            var cache = Directory.CreateDirectory(Path.Combine(job.CacheDirectory, profile));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { MaxResponseContentBufferSize = PcmAudio.MaximumWaveBytes };
            async Task<byte[]> Clip(string text, CancellationToken ct)
            {
                string file = SpeechPack.FileName(text), path = Path.Combine(cache.FullName, file), checksum = path + ".sha256";
                if (File.Exists(checksum) && VerifiedPcm(path, await File.ReadAllTextAsync(checksum, ct)) is { } cached) return cached;
                byte[]? pcm = source.TryGetValue(text, out var entry) ? VerifiedPcm(Path.Combine(job.SourcePack, file), entry.Sha256) : null;
                pcm ??= await api.GenerateAsync(client, text, ct);
                // Commit the verified response before reporting a finished clip. A later cancellation never bills it again.
                PcmAudio.Write(path, pcm);
                string temporary = checksum + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, SpeechPack.Hash(await File.ReadAllBytesAsync(path)));
                    File.Move(temporary, checksum, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return pcm;
            }
            var result = await SpeechPackBuilder.BuildAsync(stage.FullName, job.Model, job.Voice, job.Texts, Clip,
                cancellationToken: token, completedCount: progress);
            await File.WriteAllTextAsync(Path.Combine(stage.FullName, "manifest.json"),
                JsonSerializer.Serialize(result with { ApiProfile = profile }, new JsonSerializerOptions { WriteIndented = true }), token);
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

    private static PackManifest? ReadManifest(string directory)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 262144) throw new PackGenerationException("source");
        var pack = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path));
        if (pack is not { Schema: 1, Entries: not null } || pack.Entries.Length > 256
            || pack.Entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Text) || e.File != SpeechPack.FileName(e.Text)))
            throw new PackGenerationException("source");
        return pack;
    }

    private static byte[]? VerifiedPcm(string path, string sha)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is < 44 or > PcmAudio.MaximumWaveBytes) return null;
        byte[] data = File.ReadAllBytes(path);
        if (sha != SpeechPack.Hash(data)) return null;
        using var wave = new MemoryStream(data, writable: false);
        try { return PcmAudio.Read(wave); } catch (InvalidDataException) { return null; }
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
            else File.Copy(entry.FullName, Path.Combine(destination.FullName, entry.Name));
        }
    }

    private static void CleanUp(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Console.Error.WriteLine("Temporary generation directory could not be removed."); }
    }
}
