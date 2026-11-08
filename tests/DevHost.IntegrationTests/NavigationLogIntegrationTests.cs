using ETS2LA.Backend;
using ETS2LA.Logging;

namespace DevHost.IntegrationTests;

public sealed class NavigationLogIntegrationTests(LocalHostFixture fixture) : IClassFixture<LocalHostFixture>
{
    [Fact]
    public async Task EnabledPluginLogsReadinessAndWorkerStopsWithoutGameData()
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
        Logger.OnLog += Observe;
        try
        {
            handler.LoadPlugins();
            var plugin = Assert.Single(handler.LoadedPlugins);
            plugin.OnEnable();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            plugin.OnDisable();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(plugin._IsRunning);
        }
        finally
        {
            handler.UnloadPlugins();
            Logger.OnLog -= Observe;
        }
    }
}
