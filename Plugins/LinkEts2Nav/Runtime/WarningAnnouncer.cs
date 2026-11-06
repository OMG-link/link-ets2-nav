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
    private readonly Action<string> warn;
    private readonly CancellationToken cancellationToken;

    public Ets2LaWarningAnnouncer(Action? play = null, Func<string?>? audioFile = null,
        Action<string>? playFile = null, Action<string>? info = null, Action<string>? warning = null,
        CancellationToken cancellationToken = default)
    {
        this.play = play ?? (() => AudioHandler.Current.Play(DefaultAudio.PreAlert));
        this.audioFile = audioFile ?? (() => WarningAudioOptions.LoadFilePath(AppContext.BaseDirectory));
        this.playFile = playFile ?? (path => AudioHandler.Current.Queue(path));
        this.info = info ?? (message => Logger.Info(Escape(message)));
        warn = warning ?? (message => Logger.Warn(Escape(message)));
        this.cancellationToken = cancellationToken;
    }

    private static string Escape(string text) => "[[LinkEts2Nav]] " + text.Replace("[", "[[").Replace("]", "]]");

    public void Announce(CameraWarning warning) => TryPlay();

    /// <summary>True means a playback request was submitted, not that the device has played it.</summary>
    public bool TryPlay()
    {
        if (cancellationToken.IsCancellationRequested) return false;
        try
        {
            string? file = null;
            try { file = audioFile(); }
            catch (Exception ex) { warn("预警音频配置无法读取，回退内置提示音：" + ex.Message); }
            if (file is not null)
            {
                if (!File.Exists(file)) warn("自定义预警音频不存在，回退内置提示音：" + file);
                else
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        playFile(file);
                        info("自定义预警音频已提交播放：" + file);
                        return true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
                    catch (Exception ex) { warn("自定义音频提交失败，回退内置提示音：" + ex.Message); }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            play();
            info("内置预警提示音已提交播放。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            warn("预警音频不可用：" + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }
}
