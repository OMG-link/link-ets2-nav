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

    public override void Init() => Logger.Info("[[LinkEts2Nav]] Plugin loaded. Diagnostics: /link-ets2-nav");

    // Own an async worker rather than restarting the host's non-joinable Tick thread.
    public override void OnEnable()
    {
        lock (lifecycle)
        {
            if (_IsRunning) return;
            _IsRunning = true;
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
                    Logger.Warn($"[[LinkEts2Nav]] Worker stopped: {ex.GetType().Name}");
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
        lock (lifecycle)
        {
            _IsRunning = false;
            stop?.Cancel();
        }
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
        var pipeline = new WarningPipeline(new Ets2LaWarningAnnouncer(() =>
        {
            if (!token.IsCancellationRequested) AudioHandler.Current.Play(DefaultAudio.PreAlert);
        }));
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
                    if (!token.IsCancellationRequested)
                        NavDiagnostics.Publish(new(now, "等待车辆数据；暂停或连接中断期间不预警。",
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
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        calibrations = CameraCalibrationSet.Empty;
                        calibrationError = "校准配置无效，已停止使用：" + ex.Message;
                    }
                }
                var currentInstallation = ApplicationState.Current.RunningGame;
                if (currentInstallation is null || currentInstallation.Type != GameType.EuroTruckSimulator2)
                {
                    NavDiagnostics.Publish(new(now, "等待 ETS2 当前地图就绪。", data.RouteAvailability.ToString(), 0, []));
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
                            mount = OfficialArchiveMount.FromGameLog(currentInstallation.Path, log);
                            scanner = new NearbyCameraScanner(mount.FileSystem);
                            installation = currentInstallation;
                            signature = nextSignature;
                        }
                    }
                    if (mount is null || scanner is null) continue;
                    if (session != data.SessionId)
                    {
                        session = data.SessionId;
                        nearby = null;
                        pipeline.Reset();
                    }
                    var position = new Vector3((float)vehicle.X, (float)vehicle.Y, (float)vehicle.Z);
                    if (nearby is null || Vector3.Distance(position, scannedPosition) >= 25 || now - scanTime >= TimeSpan.FromSeconds(3))
                    {
                        nearby = scanner.ReadNearby(position, 1100, token);
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
                    if (!token.IsCancellationRequested) NavDiagnostics.Publish(diagnostic);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!token.IsCancellationRequested) NavDiagnostics.Publish(new(DateTimeOffset.UtcNow, "地图暂不可用：" + ex.Message,
                        data.RouteAvailability.ToString(), 0, []));
                    // No tight retry loop if archives or map resources are unavailable.
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        finally { mount?.Dispose(); }
    }
}
