using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Runtime;

public interface ISpeechSynthesizer : IAsyncDisposable
{
    Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken);
}

/// <summary>One persistent child process keeps native inference out of the host and serializes requests.</summary>
public sealed class KokoroSpeechProcess(SpeechOptions options) : ISpeechSynthesizer
{
    private Process? process;
    private Task? errorPump;
    private readonly Queue<string> errors = new();
    private long nextId;

    public async Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            if (process is null || process.HasExited) Start();
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
                throw new IOException(error.GetString());
            string path = Path.GetFullPath(root.GetProperty("path").GetString() ?? throw new IOException("No speech file."));
            string cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CacheDirectory)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(cacheRoot, comparison) || Path.GetExtension(path) != ".wav")
                throw new IOException("Speech worker returned a file outside its cache.");
            using var wav = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            if (wav.Length < 44 || wav.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8)
                || !header[8..].SequenceEqual("WAVE"u8)) throw new IOException("Speech worker returned invalid WAV audio.");
            return path;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void Start()
    {
        if (!File.Exists(options.WorkerPath)) throw new FileNotFoundException("Speech worker is not deployed.", options.WorkerPath);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(options.WorkerPath)!
        };
        foreach (string argument in new[] { options.WorkerPath, "--model-dir", options.ModelDirectory!,
            "--cache-dir", options.CacheDirectory, "--voice", options.Voice, "--threads",
            options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        process?.Dispose();
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
    private void Stop()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (process is not null)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            if (errorPump is not null)
            {
                try { await errorPump.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
            }
            process.Dispose();
        }
    }
}
