using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Runtime;

public interface ISpeechSynthesizer : IAsyncDisposable
{
    Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken);
}
/// <summary>Worker-reported cache reuse; a miss may include local synthesis or fragment assembly.</summary>
public interface ISpeechSynthesisDiagnostics
{
    bool? LastCacheHit {get;}
}

internal enum SpeechUnavailableReason { MissingRecording, MissingModel, UnreadableModel }

internal sealed class SpeechUnavailableException(SpeechUnavailableReason reason, string message) : Exception(message)
{
    internal SpeechUnavailableReason Reason { get; } = reason;
}

/// <summary>One persistent child process keeps native inference out of the host and serializes requests.</summary>
public sealed class KokoroSpeechProcess(SpeechOptions options) : ISpeechSynthesizer,ISpeechSynthesisDiagnostics
{
    public bool? LastCacheHit {get;private set;}
    private Process? process;
    private Task? errorPump;
    private readonly Queue<string> errors = new();
    private long nextId;

    public async Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        LastCacheHit=null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        bool completed=false;
        try
        {
            if (process is null || process.HasExited) await StartAsync().ConfigureAwait(false);
            string id = Interlocked.Increment(ref nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, text }).AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            if (line is null || line.Length > 32768)
                throw new IOException("Speech worker closed its output. " + RecentErrors());
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (root.GetProperty("id").GetString() != id) throw new IOException("Speech worker response ID mismatch.");
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                if (options.PreparedSpeechOnly && root.TryGetProperty("preparedSpeechUnavailable", out var recording)
                    && recording.ValueKind == JsonValueKind.True)
                {
                    completed = true;
                    throw new SpeechUnavailableException(SpeechUnavailableReason.MissingRecording, "仅使用预录语音，当前语音包缺少所需录音。");
                }
                if (root.TryGetProperty("missingModelResource", out var missing) && missing.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(missing.GetString()))
                {
                    completed = true;
                    bool unreadable = root.TryGetProperty("modelResourceUnreadable", out var flag) && flag.GetBoolean();
                    throw unreadable
                        ? new SpeechUnavailableException(SpeechUnavailableReason.UnreadableModel, $"本地语音模型文件无法读取（{missing.GetString()}）。系统原因：{error.GetString()}")
                        : new SpeechUnavailableException(SpeechUnavailableReason.MissingModel, $"本地语音模型文件缺失或为空（{missing.GetString()}）。");
                }
                throw new IOException(error.GetString());
            }
            string path = Path.GetFullPath(root.GetProperty("path").GetString() ?? throw new IOException("No speech file."));
            if(root.TryGetProperty("cacheHit",out var hit)&&hit.ValueKind is JsonValueKind.True or JsonValueKind.False)
                LastCacheHit=hit.GetBoolean();
            string cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CacheDirectory)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(cacheRoot, comparison) || Path.GetExtension(path) != ".wav")
                throw new IOException("Speech worker returned a file outside its cache.");
            using var wav = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            if (wav.Length < 44 || wav.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8)
                || !header[8..].SequenceEqual("WAVE"u8)) throw new IOException("Speech worker returned invalid WAV audio.");
            completed=true;
            return path;
        }
        finally
        {
            if(!completed)Stop(process);
        }
    }

    private async Task StartAsync()
    {
        if (!File.Exists(options.WorkerPath)) throw new FileNotFoundException("Speech worker is not deployed.", options.WorkerPath);
        bool managedAssembly = string.Equals(Path.GetExtension(options.WorkerPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managedAssembly ? "dotnet" : options.WorkerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(options.WorkerPath)!
        };
        if (managedAssembly) start.ArgumentList.Add(options.WorkerPath);
        if (options.PreparedSpeechOnly) start.ArgumentList.Add("--prepared-only");
        else
        {
            start.ArgumentList.Add("--model-dir");
            start.ArgumentList.Add(options.ModelDirectory!);
        }
        foreach (string argument in new[] { "--cache-dir", options.CacheDirectory, "--pack-dir", options.PackDirectory, "--voice", options.Voice, "--threads",
            options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        await DisposeAsync().ConfigureAwait(false);
        process = Process.Start(start) ?? throw new IOException("Could not start the speech worker.");
        lock (errors) errors.Clear();
        var started = process;
        errorPump = Task.Run(async () =>
        {
            try
            {
                while (await started.StandardError.ReadLineAsync() is { } line)
                {
                    lock (errors)
                    {
                        errors.Enqueue(line.Length > 1024 ? line[..1024] : line);
                        while (errors.Count > 8) errors.Dequeue();
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        });
    }

    private string RecentErrors() { lock (errors) return string.Join(" | ", errors); }
    private static void Stop(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        using var stopped=process;
        var pump=errorPump;
        process=null;errorPump=null;
        if(stopped is null)return;
        using var input=stopped.StandardInput;
        using var output=stopped.StandardOutput;
        using var error=stopped.StandardError;
        Stop(stopped);
        try { await stopped.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        if(pump is not null)
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
        }
    }
}
