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
    public void MissingCustomFileThrowsWithoutSubmittingDefaultAudio()
    {
        int defaults = 0;
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => Path.Combine(root, "missing.wav"),
            _ => throw new InvalidOperationException(), logs.Add, logs.Add);
        var error = Assert.Throws<FileNotFoundException>(() => player.TryPlay());
        Assert.Equal(Path.Combine(root, "missing.wav"), error.FileName);
        Assert.Equal(0, defaults);
        Assert.Empty(logs);
    }

    [Fact]
    public void InvalidConfigurationPropagatesWithoutSubmittingDefaultAudio()
    {
        File.WriteAllText(Config, "{bad json");
        int defaults = 0;
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => WarningAudioOptions.LoadFilePath(root),
            info: logs.Add, warning: logs.Add);
        Assert.Throws<System.Text.Json.JsonException>(() => player.TryPlay());
        Assert.Equal(0, defaults);
        Assert.Empty(logs);
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
    public void SynchronousPlaybackFailurePropagatesWithoutSubmittingDefaultAudio()
    {
        int defaults = 0;
        string audio = Path.Combine(root, "warning.wav");
        File.WriteAllBytes(audio, [1]);
        var expected = new IOException("device unavailable");
        var player = new Ets2LaWarningAnnouncer(() => defaults++, () => audio,
            _ => throw expected, logs.Add, logs.Add);
        Assert.Same(expected, Assert.Throws<IOException>(() => player.TryPlay()));
        Assert.Equal(0, defaults);
        Assert.Empty(logs);
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
 [Theory][InlineData(false)][InlineData(true)]
 public void UnknownSelectionOrPlaybackFailuresKeepTheOriginalException(bool selecting)
 {
  var expected=new InvalidOperationException("fixture failure");int defaults=0,customs=0,logs=0;
  var announcer=new Ets2LaWarningAnnouncer(()=>{defaults++;throw expected;},()=>selecting?throw expected:null,
   _=>customs++, _=>logs++, _=>throw new InvalidOperationException("Fallback logger must not mask original failure"));
  Assert.Same(expected,Assert.Throws<InvalidOperationException>(()=>announcer.TryPlay()));Assert.Equal(0,customs);Assert.Equal(0,logs);Assert.Equal(selecting?0:1,defaults);
 }
 [Fact] public void UnknownCustomPlaybackFailureIsNotARequestForDefaultAudio()
 {
  string audio=Path.Combine(root,"custom.wav");File.WriteAllText(audio,"fixture");var expected=new InvalidOperationException("custom submission failed");int defaults=0;
  var announcer=new Ets2LaWarningAnnouncer(()=>defaults++,()=>audio,_=>throw expected,_=>{},_=>throw new InvalidOperationException("No fallback log"));
  Assert.Same(expected,Assert.Throws<InvalidOperationException>(()=>announcer.TryPlay()));Assert.Equal(0,defaults);
 }
 [Fact] public void InfoFailureDoesNotSubmitASecondSound()
 {
  var expected=new InvalidOperationException("info callback failed");int defaults=0;
  var announcer=new Ets2LaWarningAnnouncer(()=>defaults++,()=>null,info:_=>throw expected,warning:_=>throw new InvalidOperationException("No downgrade"));
  Assert.Same(expected,Assert.Throws<InvalidOperationException>(()=>announcer.TryPlay()));Assert.Equal(1,defaults);
 }
 [Fact] public void CancellationObservedAfterSelectionDoesNotSubmitAudio()
 {
  using var cancel=new CancellationTokenSource();int defaults=0;
  var announcer=new Ets2LaWarningAnnouncer(()=>defaults++,()=>{cancel.Cancel();return null;},info:_=>throw new InvalidOperationException("No log for cancelled request"),cancellationToken:cancel.Token);
  Assert.False(announcer.TryPlay());Assert.Equal(0,defaults);
 }
 [Fact] public void MatchingKnownCancellationDuringSubmissionReturnsFalse()
 {
  using var cancel=new CancellationTokenSource();
  var announcer=new Ets2LaWarningAnnouncer(()=>{cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();},()=>null,info:_=>{},cancellationToken:cancel.Token);
  Assert.False(announcer.TryPlay());
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void ForeignCancellationIsNotMistakenForOwnedShutdown(bool alsoCancelOwner)
 {
  using var owner=new CancellationTokenSource();using var foreign=new CancellationTokenSource();foreign.Cancel();var expected=new OperationCanceledException(foreign.Token);
  var announcer=new Ets2LaWarningAnnouncer(()=>{if(alsoCancelOwner)owner.Cancel();throw expected;},()=>null,info:_=>{},cancellationToken:owner.Token);
  Assert.Same(expected,Assert.Throws<OperationCanceledException>(()=>announcer.TryPlay()));
 }
}
