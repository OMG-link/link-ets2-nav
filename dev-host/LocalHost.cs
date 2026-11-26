using ETS2LA.Backend;
using ETS2LA.Game;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.State;
using System.Security.Cryptography;
using System.Text;

namespace ETS2LA;

internal static class LocalHost
{
    public static Mutex OpenInstanceLock(out bool firstInstance)
    {
        // Both launch modes run as dotnet.exe. Scope the lock to this deployment.
        var directory = Path.GetFullPath(AppContext.BaseDirectory);
        if (OperatingSystem.IsWindows()) directory = directory.ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
        return new Mutex(false, $"LinkEts2Nav-{id}", out firstInstance);
    }

    public static int RunNavigationOnly()
    {
        using var stopped = new ManualResetEventSlim();
        void Stop(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stopped.Set();
        }

        Console.CancelKeyPress += Stop;
        GameTelemetry? telemetry = null;
        ApplicationState? state = null;
        PluginBackend? backend = null;
        try
        {
            if (Utils.DoesETS2LAProcessExist())
                throw new InvalidOperationException("ETS2LA is already running.");

            AppContext.SetSwitch("LinkEts2Nav.NavigationOnly", true);
            telemetry = GameTelemetry.Current;
            state = ApplicationState.Current;
            _ = GameHandler.Current;
            backend = PluginBackend.Current;
            backend.Start();
            if (backend.PluginHandler?.EnablePlugin(pluginId: "link.ets2.nav") != true)
                throw new InvalidOperationException("Could not enable link.ets2.nav. Check the deployed Plugins directory.");

            Logger.Info("Navigation is running. Press Ctrl+C to exit.");
            stopped.Wait();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            backend?.Shutdown();
            telemetry?.Shutdown();
            state?.Shutdown();
            LogFileWriter.Current.Save();
            Console.CancelKeyPress -= Stop;
        }
    }
}
