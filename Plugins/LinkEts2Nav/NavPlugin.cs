using ETS2LA.Audio;
using ETS2LA.Backend.Events;
using ETS2LA.Game;
using ETS2LA.Logging;
using ETS2LA.Shared;
using ETS2LA.State;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using System.Numerics;

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

    private static PlannedSpeechAnnouncer? CreateSpeech(IWarningAnnouncer fallback, NavigationLogReporter log, CancellationToken token)
    {
        try
        {
            var options = SpeechOptions.Load(AppContext.BaseDirectory);
            if (!options.Enabled) return null;
            log.Event("TTS 已启用：按预警触发顺序预生成，音色=" + options.Voice + "，文件缓存=" + options.CacheDirectory, DateTimeOffset.UtcNow);
            return new(new KokoroSpeechProcess(options), new Ets2LaWarningAnnouncer(audioFile: () => null, cancellationToken: token), path => AudioHandler.Current.Queue(path),
                message => log.Event(message, DateTimeOffset.UtcNow), token,
                PreGeneratedSpeech.Load(options.PackDirectory, message => log.Event(message, DateTimeOffset.UtcNow)));
        }
        catch (Exception ex)
        {
            log.Event("TTS 配置不可用，使用现有提示音：" + ex.Message, DateTimeOffset.UtcNow);
            return null;
        }
    }

    private static async Task RunAsync(CancellationToken token)
    {
        using var source = new Ets2LaGameDataSource(Events.Current);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        var logOutput = new NavigationLogReporter();
        await using var motionTrace = new NavigationTraceRecorder(
            Path.Combine(AppContext.BaseDirectory, "Cache", "LinkEts2Nav", "Diagnostics"),
            message => logOutput.Event(message, DateTimeOffset.UtcNow));
        logOutput.Event("后台扫描已启动；状态无变化时每 30 秒汇总一次。", DateTimeOffset.UtcNow);
        var fallback = new Ets2LaWarningAnnouncer(cancellationToken: token);
        await using var speech = CreateSpeech(fallback, logOutput, token);
        var pipeline = new WarningPipeline(new LoggedAnnouncer((IWarningAnnouncer?)speech ?? fallback, logOutput), speech);
        var navigation = new NavigationRuntime(speech?.Queue, () => fallback.TryPlay(),
            message => logOutput.Event(message, DateTimeOffset.UtcNow));
        void Publish(NavDiagnosticSnapshot snapshot, bool isWarning = false)
        {
            if (token.IsCancellationRequested) return;
            var fineState = source.Capture();
            snapshot = snapshot with { LatestFine = fineState.LatestFine, LatestCameraFine = fineState.LatestCameraFine,
                FineHistory = fineState.FineHistory, DroppedFineCount = fineState.DroppedFineCount };
            if (snapshot.Navigation is null)
                snapshot = snapshot with { Navigation = navigation.Suspend("等待地图和行驶数据。", snapshot.UpdatedAt) };
            NavDiagnostics.Publish(snapshot);
            if (isWarning) logOutput.Status(snapshot.State, snapshot.UpdatedAt, isWarning: true);
            else logOutput.Report(snapshot);
        }
        var calibrations = CameraCalibrationSet.Empty;
        var calibrationPath = Path.Combine(AppContext.BaseDirectory, "Settings", "LinkEts2Nav.CameraCalibrations.json");
        var calibrationCheck = DateTimeOffset.MinValue;
        string? calibrationError = null;
        var mapScanner = new BackgroundMapScanner(cancellationToken: token);
        string[] gameLog = [];
        long resourceVersion = 0;
        long observedScanRevision = 0;
        Installation? installation = null;
        TruckLib.ScsMap.Map? installationMap = null;
        string signature = "";
        string session = "";
        var mountCheck = DateTimeOffset.MinValue;
        var setupRetryAfter = DateTimeOffset.MinValue;
        var scanBusySince = DateTimeOffset.MinValue;
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                var data = source.Capture();
                var now = DateTimeOffset.UtcNow;
                if (!data.IsVehicleUsable) speech?.ClearPlan();
                logOutput.ReportFines(source.Store.DrainFines(), data.DroppedFineCount, now);
                logOutput.ReportCurrentLimit(data, now);
                if (now >= calibrationCheck)
                {
                    calibrationCheck = now.AddSeconds(5);
                    try
                    {
                        calibrations = File.Exists(calibrationPath) ? CameraCalibrationSet.Load(calibrationPath) : CameraCalibrationSet.Empty;
                        calibrationError = null;
                        logOutput.Change("calibration", calibrations.Count == 0
                            ? "校准配置为空或不存在：有效探头仅作500米未校准接近提醒。配置位置：" + calibrationPath
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
                if (currentInstallation is null || currentInstallation.Type != GameType.EuroTruckSimulator2 || !currentInstallation.IsParsed)
                {
                    speech?.ClearPlan();
                    if (installation is not null) { mapScanner.Invalidate(); pipeline.Reset(); navigation.Reset(); installation = null; }
                    Publish(new(now, "等待 ETS2 当前地图就绪。", data.RouteAvailability.ToString(), 0, []));
                    continue;
                }
                try
                {
                    if (now < setupRetryAfter) { speech?.ClearPlan(); continue; }
                    if (!ReferenceEquals(installation, currentInstallation) || now >= mountCheck)
                    {
                        mountCheck = now.AddSeconds(5);
                        var log = currentInstallation.GetLogFileContents().ToArray();
                        string nextSignature = OfficialArchiveMount.ResolveGameLog(currentInstallation.Path, log).Signature;
                        if (!ReferenceEquals(installation, currentInstallation) || nextSignature != signature || !ReferenceEquals(installationMap, currentInstallation.GetMapData()))
                        {
                            mapScanner.Invalidate();
                            pipeline.Reset(); navigation.Reset();
                            logOutput.ResetTargets("准备复用宿主地图并补充附近缺失对象：" + currentInstallation.Path, now);
                            gameLog = log;
                            resourceVersion++;
                            installation = currentInstallation;
                            installationMap = currentInstallation.GetMapData();
                            signature = nextSignature;
                        }
                    }
                    var preparation = mapScanner.Capture();
                    if (preparation.IsPrepared)
                        logOutput.Change("map-preparation", "全局地图准备已完成。", now);
                    else if (preparation.Error is not null)
                        logOutput.Change("map-preparation", "全局地图准备失败，将重试：" + preparation.Error, now, isWarning: true);
                    if (mapScanner.Prepare(new(resourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "", currentInstallation.Path, gameLog, default) { HostMap = installationMap }, now))
                        logOutput.Event("开始全局地图准备（不等待车辆位置）。", now);
                    if (!data.CanPrepareMap(now) || data.Vehicle is not { } vehicle)
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
                    if (session != data.SessionId)
                    {
                        logOutput.ResetTargets("车辆会话变化：已重置目标和预警状态（首次连接、读档或传送等）。", now);
                        session = data.SessionId;
                        mapScanner.Invalidate();
                        pipeline.Reset(); navigation.Reset();
                    }
                    var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
                    if (mapScanner.Request(new(resourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        session, currentInstallation.Path, gameLog, position) { HostMap = currentInstallation.GetMapData() }, now))
                        logOutput.ScanStarted(position, now);
                    var scan = mapScanner.Capture();
                    if (!scan.IsScanning) scanBusySince = DateTimeOffset.MinValue;
                    else if (scanBusySince == DateTimeOffset.MinValue) scanBusySince = now;
                    var nearby = scan.Map;
                    if (scan.Revision != observedScanRevision)
                    {
                        observedScanRevision = scan.Revision;
                        if (scan.Error is not null)
                            logOutput.Change("scan-error", "后台地图扫描失败：" + scan.Error
                                + (nearby is null ? "；稍后重试。" : "；继续使用已准备地图，稍后重试。"), now, isWarning: true);
                        else if (nearby is not null)
                        {
                            logOutput.Change("scan-error", "后台地图快照已就绪。", now);
                            logOutput.Scanned(nearby, scan.Position, scan.Elapsed, now);
                        }
                    }
                    if (nearby is null)
                    {
                        speech?.ClearPlan();
                        string reason = scan.Error is not null ? "地图暂不可用：" + scan.Error
                            : vehicle.Paused ? "游戏已暂停：正在后台预加载附近地图，预警暂挂。"
                            : "正在后台准备附近地图，等待首次扫描完成。";
                        Publish(new(now, reason, data.RouteAvailability.ToString(), 0, []), isWarning: scan.Error is not null);
                        continue;
                    }
                    // Warning matching never awaits a scan. Capture current telemetry independently.
                    var latest = source.Capture();
                    if (latest.SessionId != session) { mapScanner.Invalidate(); speech?.ClearPlan(); continue; }
                    token.ThrowIfCancellationRequested();
                    var calibrated = nearby with { Cameras = nearby.Cameras.Select(calibrations.Apply).ToArray() };
                    var diagnostic = pipeline.Process(latest, calibrated, DateTimeOffset.UtcNow);
                    diagnostic = diagnostic with { Navigation = navigation.Process(latest, calibrated, DateTimeOffset.UtcNow) };
                    motionTrace.Record(latest, calibrated, diagnostic, signature, currentInstallation.Path);
                    if (latest.Vehicle is { Paused: true })
                        diagnostic = diagnostic with { State = "附近地图已准备；游戏已暂停，预警暂挂。" };
                    if (scan.IsScanning && now - scanBusySince >= TimeSpan.FromSeconds(1)
                        && latest.Vehicle is not { Paused: true })
                        diagnostic = diagnostic with { State = diagnostic.State + "；地图正在后台刷新，使用已准备快照。" };
                    if (calibrationError is not null) diagnostic = diagnostic with { State = diagnostic.State + " " + calibrationError };
                    Publish(diagnostic);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    speech?.ClearPlan();
                    Publish(new(DateTimeOffset.UtcNow, "地图暂不可用：" + ex.GetType().Name + ": " + ex.Message,
                        data.RouteAvailability.ToString(), 0, []), isWarning: true);
                    mapScanner.Invalidate();
                    pipeline.Reset(); navigation.Reset();
                    // Back off setup retries without blocking the telemetry/diagnostic loop.
                    setupRetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        finally
        {
            source.Dispose();
            logOutput.ReportFines(source.Store.DrainFines(), source.Capture().DroppedFineCount, DateTimeOffset.UtcNow);
            await mapScanner.DisposeAsync().ConfigureAwait(false);
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
