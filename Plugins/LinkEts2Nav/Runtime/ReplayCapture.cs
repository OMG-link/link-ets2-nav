using System.Text.Json;
using ETS2LA.State;
using ETS2LA.Logging;

namespace LinkEts2Nav.Runtime;

/// <summary>Local, explicit incident capture. Trace files are frozen on the recorder's I/O worker.</summary>
public static class ReplayCapture
{
    private static NavigationTraceRecorder? recorder;
    private static readonly SemaphoreSlim saves = new(1);
    public static bool IsAvailable => Volatile.Read(ref recorder) is not null;
    public static void Attach(NavigationTraceRecorder value) => Volatile.Write(ref recorder, value);
    public static void Detach(NavigationTraceRecorder value) => Interlocked.CompareExchange(ref recorder, null, value);

    public static async Task<string> SaveAsync(string? comment)
    {
        if (comment?.Length > 4000) throw new ArgumentException("评论最多 4000 字。");
        if (!await saves.WaitAsync(0)) throw new InvalidOperationException("正在保存另一份回放，请稍候。");
        try
        {
            var active = Volatile.Read(ref recorder) ?? throw new InvalidOperationException("请先启用插件，再保存回放。");
            var capturedAt = DateTimeOffset.Now;
            var diagnostic = NavDiagnostics.Current;
            var installation = ApplicationState.Current.RunningGame;
            string root = Path.Combine(AppContext.BaseDirectory, "Cache", "LinkEts2Nav", "Replays");
            string name = capturedAt.ToString("yyyy-MM-dd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
            string partial = Path.Combine(root, name + ".partial");
            string destination = Path.Combine(root, name);
            Directory.CreateDirectory(partial);
            int traces = await active.SaveSnapshotAsync(partial);
            if (traces == 0) throw new InvalidOperationException("还没有可保存的驾驶轨迹；请等待附近地图和行驶数据就绪。");
            var missing = new List<string>();
            async Task Optional(string source, string file)
            {
                try { await CopySharedAsync(source, Path.Combine(partial, file)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { missing.Add(file + ": " + ex.GetType().Name); }
            }
            await Optional(Path.Combine(AppContext.BaseDirectory, "ets2la.log"), "host-log.txt");
            if (installation is not null)
                await Optional(Path.Combine(installation.DocumentsPath, "game.log.txt"), "game-log.txt");
            else missing.Add("game-log.txt: 当前游戏不可用");
            await Optional(Path.Combine(AppContext.BaseDirectory, "Settings", "LinkEts2Nav.CameraCalibrations.json"), "camera-calibrations.json");
            await File.WriteAllTextAsync(Path.Combine(partial, "comment.txt"), comment ?? "");
            await File.WriteAllTextAsync(Path.Combine(partial, "incident.json"), JsonSerializer.Serialize(new
            {
                Schema = 1, CapturedAt = capturedAt, Comment = comment ?? "",
                PluginVersion = typeof(ReplayCapture).Assembly.GetName().Version?.ToString(),
                PluginSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(typeof(ReplayCapture).Assembly.Location))),
                GameVersion = installation?.Version, GamePath = installation?.Path,
                Diagnostic = diagnostic, MissingFiles = missing,
                ReplayRequiresMatchingGameResources = true
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Directory.Move(partial, destination);
            Logger.Info("[[LinkEts2Nav]] 回放已保存：" + destination);
            return destination;
        }
        finally { saves.Release(); }
    }

    internal static async Task CopySharedAsync(string source, string destination)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        long remaining = input.Length;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, useAsync: true);
        byte[] buffer = new byte[65536];
        while (remaining > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)));
            if (read == 0) throw new EndOfStreamException("保存期间源文件被截断：" + Path.GetFileName(source));
            await output.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }
}
