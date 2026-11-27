using System.Diagnostics;
using ETS2LA.Game;
using ETS2LA.Game.Steam;
using ETS2LA.Game.Telemetry;
using ETS2LA.Game.Utils;
using ETS2LA.Logging;

namespace LinkEts2Nav.Host;

internal sealed class GameSession(string? gamePath, string? documentsPath)
{
    private Installation? installation;
    private bool logConfirmed;
    private long nextCheck;
    private long lastActive;
    private long connection;
    private long checkedConnection;
    private long logOffset;
    private DateTime logCreated, logModified;
    private string partialLine = "";

    // Telemetry reuses one mutable object. Capture the game identity before the callback returns.
    public void Observe(GameTelemetryData data)
    {
        long now = data.sdkActive && data.scsValues.game == "ETS2" ? Stopwatch.GetTimestamp() : 0;
        long previous = Volatile.Read(ref lastActive);
        if (now != 0 && (previous == 0 || Stopwatch.GetElapsedTime(previous, now) > TimeSpan.FromSeconds(2)))
            Volatile.Write(ref connection, now);
        Volatile.Write(ref lastActive, now);
    }

    public Installation? Current()
    {
        long stamp = Volatile.Read(ref lastActive);
        if (stamp == 0 || Stopwatch.GetElapsedTime(stamp) > TimeSpan.FromSeconds(2))
            return null;
        long currentConnection = Volatile.Read(ref connection);
        if (currentConnection != checkedConnection)
        {
            checkedConnection = currentConnection;
            installation = null;
            logConfirmed = false;
            logOffset = nextCheck = 0;
            partialLine = "";
        }

        long now = Stopwatch.GetTimestamp();
        if (now < nextCheck) return logConfirmed ? installation : null;
        nextCheck = now + Stopwatch.Frequency * 2;
        bool previousState = logConfirmed;
        try
        {
            installation ??= Discover();
            logConfirmed = installation is not null && ReadGameLog(installation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Steam and the game can replace their files while a profile is switching.
            logConfirmed = false;
            logOffset = 0;
            partialLine = "";
        }
        if (logConfirmed && !previousState) Logger.Info("Connected to Euro Truck Simulator 2 at " + installation!.Path);
        return logConfirmed ? installation : null;
    }

    private bool ReadGameLog(Installation game)
    {
        var file = new FileInfo(Path.Combine(game.DocumentsPath, "game.log.txt"));
        if (!file.Exists) { logOffset = 0; partialLine = ""; return false; }
        if (file.CreationTimeUtc == logCreated && file.LastWriteTimeUtc == logModified && file.Length == logOffset)
            return logConfirmed;

        // Read only new lines while driving. Rotation and truncation start a new log.
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool appended = logOffset > 0 && file.CreationTimeUtc == logCreated && file.Length > logOffset;
        stream.Position = appended ? logOffset : 0;
        bool running = appended && logConfirmed;
        using var reader = new StreamReader(stream);
        string text = (appended ? partialLine : "") + reader.ReadToEnd();
        var lines = text.Split('\n');
        foreach (string line in lines)
        {
            int marker = line.IndexOf(" : ", StringComparison.Ordinal);
            if (marker < 0) continue;
            var message = line.AsSpan(marker + 3).TrimStart();
            if (message.StartsWith("game", StringComparison.OrdinalIgnoreCase) || message.StartsWith("exit", StringComparison.OrdinalIgnoreCase))
                running = DataUtils.IsRunningBasedOnLogs([line]);
        }
        partialLine = lines[^1];
        logOffset = stream.Position;
        logCreated = file.CreationTimeUtc;
        logModified = file.LastWriteTimeUtc;
        return running;
    }

    private Installation? Discover()
    {
        const string gameName = "Euro Truck Simulator 2";
        var path = gamePath ?? SteamHandler.FindGamesInLibraries([SteamHandler.EuroTruckSimulator2AppId])
            .GetValueOrDefault(SteamHandler.EuroTruckSimulator2AppId);
        if (path == null || GameHandler.DetectGameType(path) != GameType.EuroTruckSimulator2) return null;
        var docs = documentsPath ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), gameName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", gameName));
        return new Installation
        {
            Type = GameType.EuroTruckSimulator2,
            Path = Path.GetFullPath(path),
            DocumentsPath = docs,
            ExecutablePath = Path.Combine(path, "bin", OperatingSystem.IsWindows() ? "win_x64/eurotrucks2.exe" : "linux_x64/eurotrucks2"),
            IsManuallyAdded = gamePath is not null
        };
    }
}
