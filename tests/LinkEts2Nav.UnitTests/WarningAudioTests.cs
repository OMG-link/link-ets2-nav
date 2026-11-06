using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class WarningAudioTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LinkNavAudio-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> logs = new();
    private string Config => Path.Combine(root, "Settings", "LinkEts2Nav.Audio.json");
    public WarningAudioTests() => Directory.CreateDirectory(Path.Combine(root, "Settings"));
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void MissingConfigurationUsesBuiltinWithoutInitializingDuringConstruction()
    {
        int plays = 0;
        var player = new Ets2LaWarningAnnouncer(() => plays++,
            () => WarningAudioOptions.LoadFilePath(root), info: logs.Add, warning: logs.Add);
        Assert.Equal(0, plays);
        Assert.True(player.TryPlay());
        Assert.Equal(1, plays);
    }

    [Fact]
    public void RelativeAudioPathResolvesAgainstHostNotWorkingDirectory()
    {
        File.WriteAllText(Config, """{"filePath":"Assets/warning.wav"}""");
        Assert.Equal(Path.Combine(root, "Assets", "warning.wav"), WarningAudioOptions.LoadFilePath(root));
    }

    [Fact]
    public void CustomAudioQueuesFileAndDoesNotPlayBuiltin()
    {
        string audio = Path.Combine(root, "warning.wav");
        File.WriteAllBytes(audio, [1, 2, 3]);
        string? queued = null;
        int defaults = 0;
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => audio, path => queued = path, logs.Add, logs.Add);
        Assert.True(player.TryPlay());
        Assert.Equal(audio, queued);
        Assert.Equal(0, defaults);
        Assert.Contains(logs, text => text.Contains("自定义预警音频已提交"));
    }

    [Fact]
    public void MissingCustomFileFallsBackWithDiagnostic()
    {
        int defaults = 0;
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => Path.Combine(root, "missing.wav"),
            _ => throw new InvalidOperationException(), logs.Add, logs.Add);
        Assert.True(player.TryPlay());
        Assert.Equal(1, defaults);
        Assert.Contains(logs, text => text.Contains("不存在"));
    }

    [Fact]
    public void InvalidConfigurationFallsBackWithDiagnostic()
    {
        File.WriteAllText(Config, "{bad json");
        int defaults = 0;
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => WarningAudioOptions.LoadFilePath(root),
            info: logs.Add, warning: logs.Add);
        Assert.True(player.TryPlay());
        Assert.Equal(1, defaults);
        Assert.Contains(logs, text => text.Contains("配置无法读取"));
    }

    [Fact]
    public void FmodBankIsNotPassedToOrdinaryAudioDecoder()
    {
        File.WriteAllText(Config, """{"filePath":"radar.bank"}""");
        Assert.Throws<InvalidDataException>(() => WarningAudioOptions.LoadFilePath(root));
    }

    [Fact]
    public void CancelledWorkerDoesNotSubmitAnyAudio()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var player = new Ets2LaWarningAnnouncer(() => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), info: logs.Add, warning: logs.Add, cancellationToken: cancel.Token);
        Assert.False(player.TryPlay());
        Assert.Empty(logs);
    }

    [Fact]
    public void SynchronousPlaybackFailuresAreReportedAndFallBack()
    {
        int defaults = 0;
        string audio = Path.Combine(root, "warning.wav");
        File.WriteAllBytes(audio, [1]);
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => audio,
            _ => throw new IOException("device unavailable"), logs.Add, logs.Add);
        Assert.True(player.TryPlay());
        Assert.Equal(1, defaults);
        Assert.Contains(logs, text => text.Contains("提交失败"));
    }

    [Fact]
    public void SelectionIsReloadedForEachPlaybackRequest()
    {
        string? selection = null;
        int defaults = 0, custom = 0;
        string audio = Path.Combine(root, "warning.wav");
        File.WriteAllBytes(audio, [1]);
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => selection, _ => custom++, logs.Add, logs.Add);
        player.TryPlay();
        selection = audio;
        player.TryPlay();
        Assert.Equal(1, defaults);
        Assert.Equal(1, custom);
    }
}
