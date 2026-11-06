using ETS2LA.Backend.Events;
using ETS2LA.Audio;
using ETS2LA.Game;
using ETS2LA.Logging;
using ETS2LA.Shared;
using ETS2LA.State;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LinkEts2Nav;

/// <summary>Read-only camera discovery and a conservatively gated warning pipeline.</summary>
public sealed class NavPlugin : Plugin
{
    private readonly object lifecycle = new();
    private CancellationTokenSource? stop;
    private Task worker = Task.CompletedTask;

    public override PluginInformation Info => new()
    {
        Id = "link.ets2.nav",
        Version = "0.2.0",
        Name = "Link ETS2 Nav",
        Description = "Fixed speed-camera discovery, directed warning rules and diagnostics.",
        AuthorName = "Link",
    };

    public override void Init() => Logger.Info("[[LinkEts2Nav]] 插件已加载（尚不代表已启用）；请在插件管理中启用。诊断页：/link-ets2-nav");

    // Own an async worker rather than restarting the host's non-joinable Tick thread.
    public override void OnEnable()
    {
        lock (lifecycle)
        {
            if (_IsRunning) return;
            _IsRunning = true;
            Logger.Info("[[LinkEts2Nav]] 插件已启用，准备启动后台扫描。");
            var previous = worker;
            var next = new CancellationTokenSource();
            stop = next;
            worker = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    next.Token.ThrowIfCancellationRequested();
                    await RunAsync(next.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (next.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    new NavigationLogReporter().Status("后台任务异常退出：" + ex, DateTimeOffset.UtcNow, isWarning: true);
                    if (!next.IsCancellationRequested)
                        NavDiagnostics.Publish(new(DateTimeOffset.UtcNow, "插件发生错误：" + ex.Message, "Unknown", 0, []));
                }
                finally
                {
                    lock (lifecycle)
                    {
                        if (ReferenceEquals(stop, next)) { stop = null; _IsRunning = false; }
                        next.Dispose();
                    }
                }
            });
        }
    }

    public override void OnDisable()
    {
        bool wasRunning;
        lock (lifecycle)
        {
            wasRunning = _IsRunning;
            _IsRunning = false;
            stop?.Cancel();
        }
        if (wasRunning) Logger.Info("[[LinkEts2Nav]] 插件已停用，已请求取消后台扫描。");
        NavDiagnostics.Publish(new(DateTimeOffset.UtcNow, "插件已停用。", "Unknown", 0, []));
    }

    public override void Shutdown() => OnDisable();

    private static string MountSignature(IEnumerable<string> log)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            log.Where(l => l.Contains("[fs] device ", StringComparison.Ordinal) || l.Contains("Unmounted", StringComparison.OrdinalIgnoreCase))))));

    private static async Task RunAsync(CancellationToken token)
    {
        using var source = new Ets2LaGameDataSource(Events.Current);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        var logOutput = new NavigationLogReporter();
        logOutput.Event("后台扫描已启动；状态无变化时每 30 秒汇总一次。", DateTimeOffset.UtcNow);
        var pipeline = new WarningPipeline(new LoggedAnnouncer(new Ets2LaWarningAnnouncer(() =>
        {
            if (!token.IsCancellationRequested) AudioHandler.Current.Play(DefaultAudio.PreAlert);
        }), logOutput));
        void Publish(NavDiagnosticSnapshot snapshot, bool isWarning = false)
        {
            if (token.IsCancellationRequested) return;
            NavDiagnostics.Publish(snapshot);
            if (isWarning) logOutput.Status(snapshot.State, snapshot.UpdatedAt, isWarning: true);
            else logOutput.Report(snapshot);
        }
        var calibrations = CameraCalibrationSet.Empty;
        var calibrationPath = Path.Combine(AppContext.BaseDirectory, "Settings", "LinkEts2Nav.CameraCalibrations.json");
        var calibrationCheck = DateTimeOffset.MinValue;
        string? calibrationError = null;
        OfficialArchiveMount? mount = null;
        NearbyCameraScanner? scanner = null;
        NearbyMapSnapshot? nearby = null;
        Installation? installation = null;
        string signature = "";
        string session = "";
        var mountCheck = DateTimeOffset.MinValue;
        var scanTime = DateTimeOffset.MinValue;
        var scannedPosition = new Vector3(float.PositiveInfinity);
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                var data = source.Capture();
                var now = DateTimeOffset.UtcNow;
                if (!data.IsVehicleUsable || data.Vehicle is not { } vehicle)
                {
                    string reason = data.Vehicle switch
                    {
                        null => "尚未收到遥测：请确认游戏已进入存档、SDK 可用，且本插件已经启用。",
                        { SdkActive: false } => "SDK 未激活：等待游戏或玩家资料加载。",
                        { Paused: true } => "游戏已暂停：预警暂挂。",
                        { IsFinite: false } => "遥测位置或速度无效：预警暂挂。",
                        _ => "遥测已过期或 SDK 模拟时间未推进：预警暂挂。"
                    };
                    if (!token.IsCancellationRequested)
                        Publish(new(now, reason,
                            data.RouteAvailability.ToString(), 0, []));
                    continue;
                }
                if (now >= calibrationCheck)
                {
                    calibrationCheck = now.AddSeconds(5);
                    try
                    {
                        calibrations = File.Exists(calibrationPath) ? CameraCalibrationSet.Load(calibrationPath) : CameraCalibrationSet.Empty;
                        calibrationError = null;
                        logOutput.Change("calibration", calibrations.Count == 0
                            ? "校准配置为空或不存在：当前仅识别探头，未校准目标不会播报。配置位置：" + calibrationPath
                            : $"已加载 {calibrations.Count} 条模型校准配置。", now);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        calibrations = CameraCalibrationSet.Empty;
                        calibrationError = "校准配置无效，已停止使用：" + ex.Message;
                        logOutput.Change("calibration", calibrationError, now, isWarning: true);
                    }
                }
                var currentInstallation = ApplicationState.Current.RunningGame;
                if (currentInstallation is null || currentInstallation.Type != GameType.EuroTruckSimulator2)
                {
                    Publish(new(now, "等待 ETS2 当前地图就绪。", data.RouteAvailability.ToString(), 0, []));
                    continue;
                }
                try
                {
                    if (!ReferenceEquals(installation, currentInstallation) || now >= mountCheck)
                    {
                        mountCheck = now.AddSeconds(5);
                        var log = currentInstallation.GetLogFileContents();
                        string nextSignature = MountSignature(log);
                        if (!ReferenceEquals(installation, currentInstallation) || nextSignature != signature || mount is null)
                        {
                            mount?.Dispose(); mount = null; scanner = null; nearby = null;
                            pipeline.Reset();
                            logOutput.Change("mount-attempt", "准备按当前游戏日志挂载地图资源：" + currentInstallation.Path, now);
                            mount = OfficialArchiveMount.FromGameLog(currentInstallation.Path, log);
                            logOutput.ResetTargets($"地图资源就绪：独立挂载 {mount.ArchivePaths.Count} 个官方归档。", now);
                            scanner = new NearbyCameraScanner(mount.FileSystem);
                            installation = currentInstallation;
                            signature = nextSignature;
                        }
                    }
                    if (mount is null || scanner is null) continue;
                    if (session != data.SessionId)
                    {
                        logOutput.ResetTargets("车辆会话变化：已重置目标和预警状态（首次连接、读档或传送等）。", now);
                        session = data.SessionId;
                        nearby = null;
                        pipeline.Reset();
                    }
                    var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
                    if (nearby is null || Vector3.Distance(position, scannedPosition) >= 25 || now - scanTime >= TimeSpan.FromSeconds(3))
                    {
                        logOutput.ScanStarted(position, now);
                        var scanStarted = System.Diagnostics.Stopwatch.StartNew();
                        nearby = scanner.ReadNearby(position, 1100, token);
                        logOutput.Scanned(nearby, position, scanStarted.Elapsed, DateTimeOffset.UtcNow);
                        scannedPosition = position;
                        scanTime = now;
                    }
                    // Disk reads may take time; recapture to honor pause, teleport and sample freshness.
                    var latest = source.Capture();
                    if (latest.SessionId != session) { nearby = null; continue; }
                    token.ThrowIfCancellationRequested();
                    var calibrated = nearby with { Cameras = nearby.Cameras.Select(calibrations.Apply).ToArray() };
                    var diagnostic = pipeline.Process(latest, calibrated, DateTimeOffset.UtcNow);
                    if (calibrationError is not null) diagnostic = diagnostic with { State = diagnostic.State + " " + calibrationError };
                    Publish(diagnostic);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Publish(new(DateTimeOffset.UtcNow, "地图暂不可用：" + ex.GetType().Name + ": " + ex.Message,
                        data.RouteAvailability.ToString(), 0, []), isWarning: true);
                    // No tight retry loop if archives or map resources are unavailable.
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        finally
        {
            mount?.Dispose();
            logOutput.Event("后台扫描已停止。", DateTimeOffset.UtcNow);
        }
    }
    private sealed class LoggedAnnouncer(IWarningAnnouncer inner, NavigationLogReporter log) : IWarningAnnouncer
    {
        public void Announce(LinkEts2Nav.Core.CameraWarning warning)
        {
            log.Triggered(warning, DateTimeOffset.UtcNow);
            inner.Announce(warning);
        }
    }
}
