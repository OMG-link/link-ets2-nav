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
    private bool shuttingDown;

    public override PluginInformation Info => new()
    {
        Id = "link.ets2.nav",
        Version = "0.2.0",
        Name = "Link ETS2 Nav",
        Description = "Navigation, speed-camera alerts and speech settings.",
        PluginPageButtonName = "导航",
        AuthorName = "Link",
    };

    public override void Init()
    {
        PlayerSettingsSession.Initialize();
        Logger.Info("[[LinkEts2Nav]] 插件已加载。请在插件管理中启用。导航页 /link-ets2-nav");
    }

    // Own an async worker rather than restarting the host's non-joinable Tick thread.
    public override void OnEnable()
    {
        lock (lifecycle)
        {
            if (_IsRunning || shuttingDown) return;
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
                    await RunAsync(() => ApplicationState.Current.RunningGame, standalone: false, next.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (next.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    string failure="[LinkEts2Nav] FATAL 后台任务异常退出："+ex;
                    Logger.Fatal(ex,failure.Replace("[","[[").Replace("]","]]"));
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

    public override void Shutdown()
    {
        Task active;
        lock (lifecycle)
        {
            shuttingDown = true;
            active = worker;
        }
        try
        {
            OnDisable();
            active.GetAwaiter().GetResult();
        }
        finally { PlayerSettingsSession.CloseAsync().GetAwaiter().GetResult(); }
    }

    private static PlannedSpeechAnnouncer? CreateSpeech(NavigationLogReporter log, CancellationToken token,out IReadOnlySet<string> cityNames,out ReloadableSpeech? resources,SpeechUsageCollector? usage=null)
    {
        cityNames=new HashSet<string>(StringComparer.Ordinal);
        resources=null;
        var options = SpeechOptions.Load(AppContext.BaseDirectory);
        if (!options.Enabled) return null;
        resources=new ReloadableSpeech(options,SpeechPackSettings.Current,message=>log.Event(message,DateTimeOffset.UtcNow));
        cityNames=resources.Names;
        log.Event("TTS 已启用：按预警触发顺序预生成，音色=" + options.Voice + "，文件缓存=" + options.CacheDirectory, DateTimeOffset.UtcNow);
        return new(resources, path => AudioHandler.Current.Queue(path),
            message => log.Event(message, DateTimeOffset.UtcNow), token,
            resources.Fixed,usage);
    }

    /// <summary>Runs navigation with telemetry supplied by the standalone host.</summary>
    public static Task RunStandaloneAsync(Func<Installation?> currentGame, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(currentGame);
        return RunAsync(currentGame, standalone: true, token);
    }

    private static async Task RunAsync(Func<Installation?> currentGame, bool standalone, CancellationToken token)
    {
        using var source = new Ets2LaGameDataSource(Events.Current);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        bool navigationOnly = standalone || (AppContext.TryGetSwitch("LinkEts2Nav.NavigationOnly", out bool enabled) && enabled);
        (string Resource,string? Session,DateTimeOffset ReceivedAt)? lastScanSample=null;
        var logOutput = new NavigationLogReporter();
        await using var speechUsage=new SpeechUsageStore(Path.Combine(AppContext.BaseDirectory,"Cache","LinkEts2Nav","SpeechUsage"),
            message=>logOutput.Event(message,DateTimeOffset.UtcNow),
            warning:message=>logOutput.Change("speech-usage-corrupt",message,DateTimeOffset.UtcNow,isWarning:true));
        SpeechUsageReports.Attach(speechUsage);
        await using var motionTrace = navigationOnly ? null : new NavigationTraceRecorder(
            Path.Combine(AppContext.BaseDirectory, "Cache", "LinkEts2Nav", "Diagnostics"),
            message => logOutput.Event(message, DateTimeOffset.UtcNow));
        await using var speech = CreateSpeech(logOutput, token,out var cityNames,out var speechResources,speechUsage.Collector);
        await using var playback=new GuidancePlaybackManager(speech?.Queue,new Ets2LaGuidanceAudioPlayer(),
            ()=>Ets2LaGuidanceAudioPlayer.FallbackFile(AppContext.BaseDirectory,message=>logOutput.Event(message,DateTimeOffset.UtcNow),allowCustom:speech is null),
            message=>logOutput.Event(message,DateTimeOffset.UtcNow),token,speechUsage.Collector);
        var guidance=new GuidanceCoordinator(speech?.Queue,playback,message=>logOutput.Event(message,DateTimeOffset.UtcNow));
        var pipeline = new WarningPipeline(speech,guidance);
        var navigation = new NavigationRuntime(speech?.Queue,
            message => logOutput.Event(message, DateTimeOffset.UtcNow),guidance,cityNames);
        var signalOptions=TrafficLightReminderOptions.Load(AppContext.BaseDirectory);
        var trafficLightReminder=new TrafficLightReminderRuntime(guidance,signalOptions);
        void Publish(NavDiagnosticSnapshot snapshot, bool isWarning = false)
        {
            if (token.IsCancellationRequested) return;
            var fineState = source.Capture();
            if(snapshot.TrafficLight is null)
                snapshot=snapshot with{TrafficLight=trafficLightReminder.Suspend(fineState,snapshot.UpdatedAt,"等待地图和受控车道数据")};
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
        BackgroundMapScanner? mapScanner = null;
        guidance.JourneyEstimateProvider=data=>mapScanner?.Capture().Estimate(data.Route);
        string[] gameLog = [];
        long resourceVersion = 0;
        long observedScanRevision = 0;
        Installation? installation = null;
        Installation? checkedInstallation = null;
        TruckLib.ScsMap.Map? installationMap = null;
        string signature = "";
        string session = "";
        var mountCheck = DateTimeOffset.MinValue;
        var scanBusySince = DateTimeOffset.MinValue;

        async ValueTask ReleaseMapAsync()
        {
            speech?.ClearPlan();
            if (mapScanner is not { } previousScanner) return;

            mapScanner = null;
            installation = null;
            installationMap = null;
            gameLog = [];
            observedScanRevision = 0;
            scanBusySince = DateTimeOffset.MinValue;
            pipeline.Reset();
            navigation.Reset();
            guidance.Reset();
            await previousScanner.DisposeAsync();
        }

        if (motionTrace is not null) ReplayCapture.Attach(motionTrace);
        try
        {
            logOutput.Event("后台扫描已启动；状态无变化时每 30 秒汇总一次。", DateTimeOffset.UtcNow);
            do
            {
                token.ThrowIfCancellationRequested();
                mapScanner?.ThrowIfFaulted();
                playback.ThrowIfFaulted();
                motionTrace?.ThrowIfFaulted();
                speechUsage.ThrowIfFaulted();
                if(speechResources is not null&&await speechResources.ApplyPendingAsync(speech!.Queue))
                {
                    navigation.RefreshSpeechNames();
                    logOutput.Event("预生成语音包已切换："+SpeechPackSettings.Current.Read().ActiveDirectory,DateTimeOffset.UtcNow);
                }
                var data = source.Capture();
                var now = DateTimeOffset.UtcNow;
                guidance.Observe(data,now,processJourney:false);
                if (!data.IsVehicleUsable) speech?.ClearPlan();
                logOutput.ReportFines(source.Store.DrainFines(), data.DroppedFineCount, now);
                logOutput.ReportCurrentLimit(data, now);
                if (now >= calibrationCheck)
                {
                    calibrationCheck = now.AddSeconds(5);
                    calibrations = CameraCalibrationSet.Load(calibrationPath);
                    logOutput.Change("calibration", calibrations.Count == 0
                        ? "校准配置为空或不存在：有效探头仅作500米未校准接近提醒。配置位置：" + calibrationPath
                        : $"已加载 {calibrations.Count} 条模型校准配置。", now);
                }
                var currentInstallation = currentGame();
                if (currentInstallation is null || currentInstallation.Type != GameType.EuroTruckSimulator2 || (!standalone && !currentInstallation.IsParsed))
                {
                    checkedInstallation = null;
                    await ReleaseMapAsync();
                    Publish(new(now, standalone ? "等待 ETS2 进入游戏。" : "等待 ETS2 当前地图就绪。", data.RouteAvailability.ToString(), 0, []));
                    continue;
                }
                {
                    if (!ReferenceEquals(checkedInstallation, currentInstallation) || now >= mountCheck)
                    {
                        checkedInstallation = currentInstallation;
                        mountCheck = now.AddSeconds(5);
                        var log = currentInstallation.GetLogFileContents().ToArray();
                        var mount = OfficialArchiveMount.ResolveGameLog(currentInstallation.Path, log);
                        if (mount.ArchivePaths.Count == 0)
                        {
                            // The host can still report the installation while ETS2 unloads its archives.
                            await ReleaseMapAsync();
                        }
                        else
                        {
                            string nextSignature = mount.Signature;
                            var hostMap = standalone ? null : currentInstallation.GetMapData();
                            mapScanner ??= new BackgroundMapScanner(cancellationToken: token);
                            if (!ReferenceEquals(installation, currentInstallation) || nextSignature != signature || !ReferenceEquals(installationMap, hostMap))
                            {
                                mapScanner.Invalidate();
                                pipeline.Reset(); navigation.Reset(); guidance.Reset();
                                logOutput.ResetTargets((standalone ? "准备导航地图：" : "准备复用宿主地图并补充附近缺失对象：") + currentInstallation.Path, now);
                                gameLog = log;
                                resourceVersion++;
                                installation = currentInstallation;
                                installationMap = hostMap;
                                signature = nextSignature;
                            }
                        }
                    }
                    if (mapScanner is null)
                    {
                        Publish(new(now, "等待 ETS2 加载地图资源。", data.RouteAvailability.ToString(), 0, []));
                        continue;
                    }
                    if (session != data.SessionId)
                    {
                        logOutput.ResetTargets("车辆会话变化：已重置目标和预警状态（首次连接、读档或传送等）。", now);
                        session = data.SessionId;
                        mapScanner.Invalidate();
                        pipeline.Reset(); navigation.Reset();
                        if(!guidance.HasVerifiedFerryContinuation(data))guidance.Reset();
                    }
                    mapScanner.ObserveRoute(data.NavigationRoute.Generation);
                    var preparation = mapScanner.AdoptReady();
                    if (preparation.IsPrepared)
                        logOutput.Change("map-preparation", standalone?"附近导航资料已准备好。":"全局地图准备已完成。", now);
                    else if (preparation.Error is not null)
                        logOutput.Change("map-preparation", "全局地图准备失败，将重试：" + preparation.Error, now, isWarning: true);
                    if (mapScanner.Prepare(new(resourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "", currentInstallation.Path, gameLog, default) { HostMap = installationMap, UseNavigationIndex = standalone }, now))
                        logOutput.Event(standalone?"开始准备附近导航资料。":"开始全局地图准备（不等待车辆位置）。", now);
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
                    // Announce after startup/session resets, before waiting for the nearby map scan.
                    guidance.Observe(data,now);
                    logOutput.Change("scan-coverage", data.RouteAvailability == RouteAvailability.Available
                        ? "地图扫描范围：附近 1100 m，并沿游戏导航路线预读前方 "+(MapScanCoverage.LookaheadMeters/1000).ToString(System.Globalization.CultureInfo.InvariantCulture)+" km；后台优先准备附近地图。"
                        : "地图扫描范围：附近 1100 m（无有效导航路线）。", now);
                    var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
                    var scanSample=(resourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),session,vehicle.ReceivedAt);
                    if(lastScanSample!=scanSample)
                    {
                    lastScanSample=scanSample;
                    if (mapScanner.Request(new(scanSample.Item1,
                        session, currentInstallation.Path, gameLog, position) {
                        HostMap = standalone ? null : currentInstallation.GetMapData(),
                        UseNavigationIndex = standalone,
                        Route = data.RouteAvailability == RouteAvailability.Available ? data.Route : null,
                        RouteGeneration=data.NavigationRoute.Generation,
                        NavigationRemainingMeters = vehicle.NavigationDistanceRemainingMeters }, now))
                        logOutput.ScanStarted(position, now);
                    }
                    var scan = mapScanner.AdoptReady();
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
                    mapScanner.ObserveRoute(latest.NavigationRoute.Generation);
                    guidance.Observe(latest,DateTimeOffset.UtcNow);
                    var calibrated = nearby with { Cameras = nearby.Cameras.Select(calibrations.Apply).ToArray() };
                    var navigationDiagnostic=navigation.Process(latest,calibrated,DateTimeOffset.UtcNow);
                    source.Store.ExpectFerry(navigation.ExpectedFerry);
                    var diagnostic=pipeline.Process(latest,calibrated,DateTimeOffset.UtcNow) with{Navigation=navigationDiagnostic,
                        TrafficLight=trafficLightReminder.Process(latest,calibrated,navigationDiagnostic,DateTimeOffset.UtcNow)};
                    guidance.Tick(latest,DateTimeOffset.UtcNow);
                    diagnostic=diagnostic with{Guidance=guidance.Diagnostic};
                    motionTrace?.Record(latest, calibrated, diagnostic, signature, currentInstallation.Path);
                    if (latest.Vehicle is { Paused: true })
                        diagnostic = diagnostic with { State = "附近地图已准备；游戏已暂停，预警暂挂。" };
                    if (scan.IsScanning && now - scanBusySince >= TimeSpan.FromSeconds(1)
                        && latest.Vehicle is not { Paused: true })
                        diagnostic = diagnostic with { State = diagnostic.State + "；地图正在后台刷新，使用已准备快照。" };
                    Publish(diagnostic);
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        finally
        {
            try
            {
                source.Dispose();
                logOutput.ReportFines(source.Store.DrainFines(), source.Store.Capture(DateTimeOffset.UtcNow).DroppedFineCount, DateTimeOffset.UtcNow);
            }
            finally
            {
                try
                {
                    if (motionTrace is not null) ReplayCapture.Detach(motionTrace);
                    logOutput.Event("后台扫描已停止。", DateTimeOffset.UtcNow);
                }
                finally
                {
                    if (mapScanner is not null) await mapScanner.DisposeAsync();
                }
            }
        }
    }
}
