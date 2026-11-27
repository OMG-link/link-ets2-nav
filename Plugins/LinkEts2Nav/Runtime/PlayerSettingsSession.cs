using LinkEts2Nav.Ui;

namespace LinkEts2Nav.Runtime;

/// <summary>The stock plugin opens its local settings site on demand and owns it until unload.</summary>
public static class PlayerSettingsSession
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static PlayerSettingsServer? server;
    private static volatile bool closed;

    internal static void Initialize() => closed = false;

    public static async Task<Uri> OpenAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closed) throw new InvalidOperationException("请先加载导航插件。");
            server ??= await PlayerSettingsServer.StartAsync().ConfigureAwait(false);
            return server.Address;
        }
        finally { gate.Release(); }
    }

    internal static async Task CloseAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            closed = true;
            var closing = server;
            server = null;
            if (closing is not null) await closing.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
