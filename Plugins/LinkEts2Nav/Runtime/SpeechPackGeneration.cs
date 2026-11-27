using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Runtime;

public sealed record SpeechPackGenerationRequest(string Endpoint, string Model, string Voice, string? ApiKey, int Top = 20,
    string? RequestTemplate = null, string AuthHeader = "Authorization", string AuthPrefix = "Bearer ")
{
    public override string ToString() => nameof(SpeechPackGenerationRequest);
}
public enum SpeechPackGenerationPhase { Idle, Running, Succeeded, Failed, Cancelled }
public sealed record SpeechPackGenerationStatus(SpeechPackGenerationPhase Phase, int Completed, int Total, string Message, string? OutputDirectory);

/// <summary>One private generation job per host. Pages can reconnect and observe its state without resubmitting a request.</summary>
public sealed class SpeechPackGeneration
{
    public const string DefaultRequestTemplate = """{"model":"{{model}}","voice":"{{voice}}","input":"{{text}}","response_format":"wav"}""";
    public static SpeechPackGeneration Current { get; } = new(AppContext.BaseDirectory, SpeechPackSettings.Current,
        () => SpeechUsageReports.Current?.Collector.Capture());
    private readonly string host;
    private readonly SpeechPackSettings settings;
    private readonly Func<SpeechUsageSnapshot?> liveUsage;
    private readonly string worker;
    private readonly object gate = new();
    private readonly SemaphoreSlim selectionWriter = new(1, 1);
    private string? reservedDirectory;
    private CancellationTokenSource? active;
    private bool applying;
    private SpeechPackGenerationStatus status = new(SpeechPackGenerationPhase.Idle, 0, 0, "", null);
    public SpeechPackGenerationStatus Status { get { lock (gate) return status; } }

    internal SpeechPackGeneration(string host, SpeechPackSettings settings, Func<SpeechUsageSnapshot?> liveUsage, string? worker = null)
    {
        this.host = Path.GetFullPath(host); this.settings = settings; this.liveUsage = liveUsage;
        string dll = Path.GetFullPath(SpeechOptions.DefaultWorker, this.host), exe = Path.ChangeExtension(dll, ".exe");
        this.worker = worker ?? (File.Exists(exe) ? exe : dll);
    }

    public bool TryStart(SpeechPackGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (active is not null) return false;
            var stop = active = new();
            applying = false;
            status = new(SpeechPackGenerationPhase.Running, 0, 0, "正在读取语音播放统计。", null);
            _ = Task.Run(() => GenerateAsync(request, stop));
            return true;
        }
    }

    public bool TryCancel()
    {
        lock (gate)
        {
            if (active is null || applying || active.IsCancellationRequested) return false;
            active.Cancel();
            status = status with { Message = "正在取消生成。" };
            return true;
        }
    }

    public async Task<SpeechPackSelection> SelectPackAsync(string directory, CancellationToken token = default)
    {
        await selectionWriter.WaitAsync(token);
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim(), host));
                lock (gate)
                    if (reservedDirectory is { } reserved && UsesDirectory(resolved, reserved))
                        throw new InvalidOperationException("该语音包目录正在生成，请在完成后再选择。");
            }
            return await settings.SaveAsync(directory, token);
        }
        finally { selectionWriter.Release(); }
    }

    private static bool UsesDirectory(string selected, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return selected.Equals(candidate, comparison) || selected.StartsWith(candidate + Path.DirectorySeparatorChar, comparison);
    }

    private sealed class Failure(string message) : Exception(message);

    private async Task GenerateAsync(SpeechPackGenerationRequest request, CancellationTokenSource stop)
    {
        var token = stop.Token;
        SpeechPackGenerationPhase phase;
        string message;
        string? applied = null;
        try
        {
            if (request.Top is < 1 or > 256) throw new Failure("短语数量须为 1 到 256。");
            string[] texts = await SelectAsync(request.Top, token);
            SpeechPackSettingView selection;
            string output;
            await selectionWriter.WaitAsync(token);
            try
            {
                selection = settings.Read();
                // A committed settings change can precede audio playback switching. Keep both directories intact.
                var used = new[] { selection.ResolvedDirectory, selection.ActiveDirectory }.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!, host))).ToArray();
                output = Enumerable.Range(1, 3).Select(i => Path.Combine(host, "SpeechPacks", "LinkEts2Nav", "Popular-" + i))
                    .First(p => !used.Any(selected => UsesDirectory(selected, p)));
                lock (gate) reservedDirectory = output;
            }
            finally { selectionWriter.Release(); }
            string template = string.IsNullOrWhiteSpace(request.RequestTemplate) ? DefaultRequestTemplate : request.RequestTemplate;
            var job = new GenerationJob(request.Endpoint, request.Model, request.Voice, request.ApiKey, template, request.AuthHeader,
                request.AuthPrefix, texts, output, Path.Combine(host, "Cache", "LinkEts2Nav", "PopularSpeech"), selection.ResolvedDirectory);
            lock (gate) status = status with { Total = texts.Length, Message = "正在生成语音包。" };
            await RunWorkerAsync(job, token);
            await selectionWriter.WaitAsync(token);
            try
            {
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    applying = true;
                    status = status with { Message = "正在校验并应用语音包。" };
                }
                await settings.SaveAsync(Path.GetRelativePath(host, output));
            }
            finally { selectionWriter.Release(); }
            applied = output; phase = SpeechPackGenerationPhase.Succeeded;
            message = $"已生成并选用语音包，共 {texts.Length} 条。";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            phase = stop.IsCancellationRequested ? SpeechPackGenerationPhase.Cancelled : SpeechPackGenerationPhase.Failed;
            message = phase == SpeechPackGenerationPhase.Cancelled ? "已取消，完成的录音可在下次生成时复用。" : e switch
            {
                Failure known => known.Message,
                UnauthorizedAccessException or IOException => "读取或保存语音文件失败，请检查目录权限和可用空间。",
                JsonException or InvalidDataException => "语音统计、设置或语音包格式无效。",
                _ => "生成工具未能完成任务，请检查语音设置和工具安装。"
            };
        }
        lock (gate)
        {
            status = status with { Phase = phase, Message = message, OutputDirectory = applied };
            active = null; applying = false; reservedDirectory = null;
        }
        stop.Dispose();
    }

    private async Task<string[]> SelectAsync(int top, CancellationToken token)
    {
        var usage = liveUsage();
        if (usage is null)
        {
            string path = Path.Combine(host, "Cache", "LinkEts2Nav", "SpeechUsage", "summary.json");
            if (!File.Exists(path)) throw new Failure("尚无实际播放统计。行驶并播放导航提示后可生成。");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            usage = await JsonSerializer.DeserializeAsync<SpeechUsageSnapshot>(file, cancellationToken: token);
        }
        if (usage is not { Schema: 1, MetricVersion: 1, Entries: not null }) throw new Failure("不支持此语音统计版本。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in usage.Entries)
            if (entry is null || string.IsNullOrWhiteSpace(entry.Text) || !seen.Add(entry.Text) || entry.PlayedCount < 0
                || !double.IsFinite(entry.PreparationMilliseconds) || entry.PreparationMilliseconds < 0)
                throw new Failure("语音播放统计包含无效或重复的记录。");
        var texts = usage.Entries.Where(e => e.PlayedCount > 0).OrderByDescending(e => e.PlayedCount)
            .ThenByDescending(e => e.PreparationMilliseconds).ThenBy(e => e.Text, StringComparer.Ordinal).Take(top).Select(e => e.Text).ToArray();
        if (texts.Length == 0) throw new Failure("尚无实际播放的短语。行驶并播放导航提示后可生成。");
        return texts;
    }

    private async Task RunWorkerAsync(GenerationJob job, CancellationToken token)
    {
        if (!File.Exists(worker)) throw new Failure("未找到随导航安装的语音生成工具。");
        bool dll = string.Equals(Path.GetExtension(worker), ".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? "dotnet" : worker) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(worker)! };
        if (dll) start.ArgumentList.Add(worker);
        start.ArgumentList.Add(GenerationJob.Mode);
        using var child = new Process { StartInfo = start };
        try { if (!child.Start()) throw new Failure("语音生成工具未能启动。"); }
        catch (Win32Exception) { throw new Failure("语音生成工具未能启动。"); }
        var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(job).AsMemory(), token);
            await child.StandardInput.FlushAsync(token);
            using var cancelInput = token.Register(() => { try { child.StandardInput.Close(); } catch (IOException) { } });
            bool done = false;
            while (await child.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(token) is { } line)
            {
                var reply = JsonSerializer.Deserialize<GenerationReply>(line);
                if (reply?.Kind == "error") throw new Failure(Describe(reply));
                if (done || reply is null || reply.Kind is not ("progress" or "done") || reply.Completed < Status.Completed
                    || reply.Completed > job.Texts.Length || reply.Kind == "done" && reply.Completed != job.Texts.Length)
                    throw new Failure("语音生成工具返回了无效的进度。");
                done = reply.Kind == "done";
                lock (gate) status = status with { Completed = reply.Completed };
            }
            await child.WaitForExitAsync(token);
            if (!done || child.ExitCode != 0) throw new Failure("语音生成工具意外退出。");
        }
        finally
        {
            // EOF cooperatively cancels HTTP work and lets the worker clean its staging folder.
            try { child.StandardInput.Close(); } catch (IOException) { }
            if (!child.HasExited)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
            await errors;
        }
    }

    private static string Describe(GenerationReply reply) => reply.Code switch
    {
        "api" => "请检查 API 地址、模型和音色。",
        "template" => "请求模板须为 JSON 对象，并包含值为 {{text}} 的字符串。",
        "auth" => "请检查 API 密钥、认证请求头和前缀。",
        "wav" => "API 返回的音频须为 24 kHz、单声道、16 位 PCM WAV。",
        "http" when reply.HttpStatus is >= 100 and <= 599 => $"语音 API 返回 HTTP {reply.HttpStatus}。",
        "http" => "无法连接语音 API，或响应超过音频大小上限。",
        "timeout" => "语音 API 请求超时。",
        "storage" or "link" or "occupied" => "生成目录无法使用，请检查目录权限、文件冲突和可用空间。",
        "pack" or "source" => "语音包条目、格式或大小不符合要求。",
        _ => "语音生成工具未能完成任务。"
    };
}
