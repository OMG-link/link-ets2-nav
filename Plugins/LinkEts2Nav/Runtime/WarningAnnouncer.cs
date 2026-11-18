using ETS2LA.Audio;
using LinkEts2Nav.Core;
using ETS2LA.Logging;

namespace LinkEts2Nav.Runtime;

/// <summary>Receives structured warnings so a future TTS renderer can describe the target.</summary>
public interface IWarningAnnouncer
{
    void Announce(CameraWarning warning);
}

/// <summary>Lazily queues a local custom file or the host's bundled warning sound.</summary>
public sealed class Ets2LaWarningAnnouncer : IWarningAnnouncer
{
    private readonly Action play;
    private readonly Func<string?> audioFile;
    private readonly Action<string> playFile;
    private readonly Action<string> info;
    private readonly CancellationToken cancellationToken;

    public Ets2LaWarningAnnouncer(Action? play = null, Func<string?>? audioFile = null,
        Action<string>? playFile = null, Action<string>? info = null, Action<string>? warning = null,
        CancellationToken cancellationToken = default)
    {
        this.play = play ?? (() => AudioHandler.Current.Play(DefaultAudio.PreAlert));
        this.audioFile = audioFile ?? (() => WarningAudioOptions.LoadFilePath(AppContext.BaseDirectory));
        this.playFile = playFile ?? (path => AudioHandler.Current.Queue(path));
        this.info = info ?? (message => Logger.Info(Escape(message)));
        this.cancellationToken = cancellationToken;
    }

    private static string Escape(string text) => "[[LinkEts2Nav]] " + text.Replace("[", "[[").Replace("]", "]]");

    public void Announce(CameraWarning warning) => TryPlay();

    /// <summary>True means submitted, not played. False means cancellation; configuration and submission failures propagate.</summary>
    public bool TryPlay()
    {
        if (cancellationToken.IsCancellationRequested) return false;
        try
        {
            string? file = audioFile();
            if (file is not null)
            {
                if (!File.Exists(file)) throw new FileNotFoundException("Configured warning audio file is missing.", file);
                cancellationToken.ThrowIfCancellationRequested();
                playFile(file);
                info("自定义预警音频已提交播放：" + file);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                play();
                info("内置预警提示音已提交播放。");
            }
            return true;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken == cancellationToken)
        { return false; }
    }
}
