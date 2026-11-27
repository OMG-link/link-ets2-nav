using ETS2LA.Backend.Events;
using ETS2LA.Game;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Host;

internal static class Program
{
    private const string Usage = "LinkEts2Nav.Host [--game-path <ETS2 installation>] [--documents-path <ETS2 documents folder>]";

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string? gamePath = null, documentsPath = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return UsageError("Expected a directory after " + args[i]);
            switch (args[i])
            {
                case "--game-path" when gamePath is null: gamePath = Path.GetFullPath(args[i + 1]); break;
                case "--documents-path" when documentsPath is null: documentsPath = Path.GetFullPath(args[i + 1]); break;
                default: return UsageError("Unexpected or repeated option: " + args[i]);
            }
        }
        if (gamePath != null && GameHandler.DetectGameType(gamePath) != GameType.EuroTruckSimulator2)
            return UsageError("No Euro Truck Simulator 2 executable found under " + gamePath);

        using var instance = new Mutex(false, "LinkEts2Nav.Standalone", out bool first);
        if (!first)
        {
            Console.Error.WriteLine("The standalone navigation host is already running.");
            return 1;
        }

        // ETS2LA logging uses the working directory. Navigation assets and settings use the executable directory.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        AppContext.SetSwitch("LinkEts2Nav.NavigationOnly", true);
        using var stop = new CancellationTokenSource();
        var session = new GameSession(gamePath, documentsPath);
        void Stop(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Cancel();
        }
        Console.CancelKeyPress += Stop;
        var events = Events.Current;
        GameTelemetry? telemetry = null;
        try
        {
            events.Subscribe<GameTelemetryData>(Ets2LaGameDataSource.TelemetryTopic, session.Observe);
            telemetry = GameTelemetry.Current;
            _ = NavigationProvider.Current;
            _ = SemaphoreProvider.Current;
            Logger.Info("Standalone navigation is running. Open ETS2 with scs-telemetry and the matching ETS2LA SDK enabled. Press Ctrl+C to exit.");
            await NavPlugin.RunStandaloneAsync(session.Current, stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Logger.Error(ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            events.Unsubscribe<GameTelemetryData>(Ets2LaGameDataSource.TelemetryTopic, session.Observe);
            telemetry?.Shutdown();
            Logger.Info("Standalone navigation stopped.");
            LogFileWriter.Current.Save();
            Console.CancelKeyPress -= Stop;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
