using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Runtime;

public sealed record SpeechPackGenerationRequest(string Endpoint = "", string Model = "", string Voice = "", string? ApiKey = null,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long TokenBudget = 0,
    string? RequestTemplate = null, string AuthHeader = "Authorization", string AuthPrefix = "Bearer ",
    string? TokenCountEndpoint = null, string Source = "api")
{
    public override string ToString() => nameof(SpeechPackGenerationRequest);

    internal string? ValidationError()
    {
        if (TokenBudget <= 0) return "输入 token 预算须为正整数。";
        if (Source == "local") return null;
        if (Source != "api") return "请选择内置 Kokoro 或语音 API。";
        if (!HttpAddress(Endpoint, out var speech) || string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(Voice))
            return "请填写有效的 HTTP 或 HTTPS API 地址、模型和音色。";
        if (!string.IsNullOrWhiteSpace(TokenCountEndpoint)
            && (!HttpAddress(TokenCountEndpoint, out var counter) || speech!.Scheme != counter!.Scheme
                || speech.IdnHost != counter.IdnHost || speech.Port != counter.Port))
            return "Token 计数地址须与合成 API 使用相同的协议、主机和端口。";
        return null;
    }

    private static bool HttpAddress(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
}
public enum SpeechPackGenerationPhase { Idle, Running, Succeeded, Failed, Cancelled }
public sealed record SpeechPackGenerationStatus(SpeechPackGenerationPhase Phase, int Completed, int Total, string Message,
    string? OutputDirectory, long Budget = 0, long SpentTokens = 0, int WholeCount = 0, int ComposedCount = 0);

internal sealed class SpeechPackDirectoryReservedException() : InvalidOperationException("该语音包目录正在生成，请在完成后再选择。");

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
    private Task jobTask = Task.CompletedTask;
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
            status = new(SpeechPackGenerationPhase.Running, 0, 0, "正在读取语音播放统计。", null, request.TokenBudget);
            jobTask = Task.Run(() => GenerateAsync(request, stop));
            return true;
        }
    }

    public bool TryCancel()
    {
        lock (gate) return CancelLocked();
    }

    internal Task CancelAndWaitAsync()
    {
        lock (gate) { CancelLocked(); return jobTask; }
    }

    private bool CancelLocked()
    {
        if (active is null || applying || active.IsCancellationRequested) return false;
        active.Cancel();
        status = status with { Message = "正在取消生成。" };
        return true;
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
                        throw new SpeechPackDirectoryReservedException();
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
            if (request.ValidationError() is { } invalid) throw new Failure(invalid);
            var candidates = await ReadCandidatesAsync(token);
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
            string cache = Path.Combine(host, "Cache", "LinkEts2Nav", "PopularSpeech");
            GenerationJob job;
            if (request.Source == "local")
            {
                var options = SpeechOptions.LoadForGeneration(host);
                job = new("", "kokoro", options.Voice, null, DefaultRequestTemplate, "", "", candidates,
                    output, cache, selection.ResolvedDirectory, request.TokenBudget, LocalModelDirectory: options.ModelDirectory,
                    LocalThreads: options.Threads, LocalCacheDirectory: options.CacheDirectory);
            }
            else
                job = new(request.Endpoint, request.Model, request.Voice, request.ApiKey, template, request.AuthHeader,
                    request.AuthPrefix, candidates, output, cache, selection.ResolvedDirectory, request.TokenBudget, request.TokenCountEndpoint);
            lock (gate) status = status with { Message = "正在生成语音包。" };
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
            message = "已生成并选用语音包。";
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

    private async Task<GenerationCandidate[]> ReadCandidatesAsync(CancellationToken token)
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
        {
            token.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.Text) || (entry.PlayedCount > 0 && entry.Text.Length > 2048)
                || !seen.Add(entry.Text) || entry.PlayedCount < 0
                || !double.IsFinite(entry.PreparationMilliseconds) || entry.PreparationMilliseconds < 0)
                throw new Failure("语音播放统计包含无效或重复的记录。");
        }
        var candidates = usage.Entries.Where(e => e.PlayedCount > 0)
            .Select(e => new GenerationCandidate(e.Text, e.PlayedCount, e.PreparationMilliseconds)).ToArray();
        if (candidates.Length == 0) throw new Failure("尚无实际播放的短语。行驶并播放导航提示后可生成。");
        return candidates;
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
        Task<bool>? progress = null;
        try
        {
            var input = child.StandardInput.BaseStream;
            await JsonSerializer.SerializeAsync(input, job, cancellationToken: token);
            await input.WriteAsync("\n"u8.ToArray(), token);
            await input.FlushAsync(token);
            using var cancelInput = token.Register(() => { try { child.StandardInput.Close(); } catch (IOException) { } });
            progress = ReadProgressAsync(child.StandardOutput, job);
            bool done = await progress.WaitAsync(token);
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
            // A synthesis request can already be in flight when Cancel is clicked. Drain its reported cost before publishing the final state.
            if (token.IsCancellationRequested && progress is not null)
            {
                try { await progress; }
                catch (Exception ex) when (ex is Failure or IOException or JsonException) { }
            }
        }
    }

    private async Task<bool> ReadProgressAsync(StreamReader output, GenerationJob job)
    {
        bool done = false;
        while (await output.ReadLineAsync() is { } line)
        {
            var reply = JsonSerializer.Deserialize<GenerationReply>(line);
            if (reply?.Kind == "error") throw new Failure(Describe(reply));
            var prior = Status;
            if (done || reply is null || reply.Kind is not ("progress" or "done") || reply.Budget != job.TokenBudget
                || reply.SpentTokens < prior.SpentTokens || reply.SpentTokens > job.TokenBudget || reply.WholeCount < prior.WholeCount
                || reply.ComposedCount < prior.ComposedCount || (long)reply.WholeCount + reply.ComposedCount != reply.Completed
                || reply.Completed > reply.Total || reply.Total > job.Candidates.Length || reply.Kind == "done" && (reply.Total == 0 || reply.Completed != reply.Total))
                throw new Failure("语音生成工具返回了无效的进度。");
            done = reply.Kind == "done";
            lock (gate) status = status with { Completed = reply.Completed, Total = reply.Total, SpentTokens = reply.SpentTokens,
                WholeCount = reply.WholeCount, ComposedCount = reply.ComposedCount };
        }
        return done;
    }

    private static string Describe(GenerationReply reply) => reply.Code switch
    {
        "api" => "请检查 API 地址、模型和音色。",
        "budget" => "当前输入 token 预算不足以生成任何可用语句，请提高预算后重试。",
        "token-count" => "Token 计数失败，请检查同源地址、认证和返回的 tokens 整数。",
        "local-model" => "内置 Kokoro 模型无法使用，请检查语音模型目录及其文件。",
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
