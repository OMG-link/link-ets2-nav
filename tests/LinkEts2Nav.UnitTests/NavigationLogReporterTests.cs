using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationLogReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static CameraDiagnostic Camera(string decision = "未校准", bool verified = false, double distance = 400)
        => new("camera-1", "speedcam.20", distance, verified, decision);
    private static NavDiagnosticSnapshot Snapshot(DateTimeOffset time, params CameraDiagnostic[] cameras)
        => new(time, "扫描运行中", "Available", cameras.Length, cameras, VehiclePosition: "100, 0, 200");

    [Fact]
    public void UnchangedStateLogsImmediatelyThenOnlyEveryThirtySeconds()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add, messages.Add);
        for (int i = 0; i < 150; i++) reporter.Status("等待遥测", Now.AddMilliseconds(i * 200));
        Assert.Single(messages);
        reporter.Status("等待遥测", Now.AddSeconds(30));
        Assert.Equal(2, messages.Count);
        Assert.NotEqual(messages[0], messages[1]); // Host logger must not collapse heartbeats.
    }

    [Fact]
    public void ChangedStateLogsImmediatelyWithoutWaitingForHeartbeat()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        reporter.Status("等待地图", Now);
        reporter.Status("扫描运行中", Now.AddSeconds(1));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void RepeatedMapFailureIsVisibleWithoutFiveSecondRetrySpam()
    {
        var warnings = new List<string>();
        var reporter = new NavigationLogReporter(_ => { }, warnings.Add);
        for (int i = 0; i <= 6; i++)
            reporter.Status("地图不可用：radar 覆盖资源", Now.AddSeconds(i * 5), isWarning: true);
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, text => Assert.Contains("radar", text));
    }

    [Fact]
    public void DistanceUpdatesDoNotRepeatCameraDiscovery()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        reporter.Report(Snapshot(Now, Camera()));
        reporter.Report(Snapshot(Now.AddMilliseconds(200), Camera(distance: 395)));
        Assert.Single(messages, text => text.Contains("发现探头"));
    }

    [Fact]
    public void CalibrationOrDecisionChangesAreLoggedImmediately()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        reporter.Report(Snapshot(Now, Camera()));
        reporter.Report(Snapshot(Now.AddSeconds(1), Camera("位于导航前方", true)));
        Assert.Single(messages, text => text.Contains("目标判定变化") && text.Contains("位于导航前方"));
    }

    [Fact]
    public void PausedSnapshotsDoNotPretendCameraDisappeared()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        reporter.Report(Snapshot(Now, Camera()));
        reporter.Report(new(Now.AddSeconds(1), "暂停", "Available", 0, []));
        reporter.Report(Snapshot(Now.AddSeconds(2), Camera()));
        Assert.DoesNotContain(messages, text => text.Contains("目标离开"));
        Assert.Single(messages, text => text.Contains("发现探头"));
    }

    [Fact]
    public void LeavingActiveScanIsLoggedOnceWithoutClaimingCameraWasPassed()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        reporter.Report(Snapshot(Now, Camera()));
        reporter.Report(Snapshot(Now.AddSeconds(1)));
        reporter.Report(Snapshot(Now.AddSeconds(2)));
        Assert.Single(messages, text => text.Contains("目标离开") && text.Contains("不代表"));
    }

    [Fact]
    public void RepeatedEncountersAlwaysLogTheirActualWarningEvent()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        var warning = new CameraWarning("camera-1", 300, WarningDistanceKind.Road);
        reporter.Triggered(warning, Now);
        reporter.Triggered(warning, Now.AddMinutes(1));
        Assert.Equal(2, messages.Count);
        Assert.All(messages, text => Assert.Contains("播放请求", text));
    }

    [Fact]
    public void UnchangedScanResultsAreSummarizedWithoutLoggingEveryMovement()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add);
        var nearby = new NearbyMapSnapshot(new TruckLib.ScsMap.Map(), [], [], [new SectorCoordinate(0, 0)]);
        reporter.Scanned(nearby, Vector3.Zero, TimeSpan.FromMilliseconds(12), Now);
        reporter.Scanned(nearby, Vector3.UnitX * 25, TimeSpan.FromMilliseconds(15), Now.AddSeconds(1));
        Assert.Single(messages);
        reporter.Scanned(nearby, Vector3.UnitX * 500, TimeSpan.FromMilliseconds(10), Now.AddSeconds(30));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void CalibrationReloadLogsOnlyWhenItsOutcomeChanges()
    {
        var messages = new List<string>();
        var reporter = new NavigationLogReporter(messages.Add, messages.Add);
        reporter.Change("calibration", "无校准", Now);
        reporter.Change("calibration", "无校准", Now.AddSeconds(5));
        reporter.Change("calibration", "配置格式错误", Now.AddSeconds(10), true);
        reporter.Change("calibration", "已加载一条", Now.AddSeconds(15));
        Assert.Equal(3, messages.Count);
    }
}
