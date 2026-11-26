using ETS2LA.Backend;
using ETS2LA.Logging;

namespace DevHost.IntegrationTests;

public sealed class NavigationLogIntegrationTests(LocalHostFixture fixture) : IClassFixture<LocalHostFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledPluginLogsReadinessAndStartsReplayOnlyWithTheWindow(bool navigationOnly)
    {
        _ = fixture.Network;
        var handler = new PluginHandler();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(Tuple<string, string> entry)
        {
            if (!entry.Item2.Contains("[LinkEts2Nav]")) return;
            if (entry.Item2.Contains("后台扫描已启动")) started.TrySetResult();
            if (entry.Item2.Contains("等待 ETS2 当前地图就绪")) waiting.TrySetResult();
            if (entry.Item2.Contains("后台扫描已停止")) stopped.TrySetResult();
        }
        const string mode = "LinkEts2Nav.NavigationOnly";
        AppContext.TryGetSwitch(mode, out bool previousMode);
        AppContext.SetSwitch(mode, navigationOnly);
        Logger.OnLog += Observe;
        try
        {
            handler.LoadPlugins();
            var plugin = Assert.Single(handler.LoadedPlugins);
            plugin.OnEnable();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var replay = plugin.GetType().Assembly.GetType("LinkEts2Nav.Runtime.ReplayCapture", throwOnError: true)!;
            Assert.Equal(!navigationOnly, Assert.IsType<bool>(replay.GetProperty("IsAvailable")!.GetValue(null)));
            plugin.OnDisable();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(plugin._IsRunning);
        }
        finally
        {
            handler.UnloadPlugins();
            Logger.OnLog -= Observe;
            AppContext.SetSwitch(mode, previousMode);
        }
    }
}
