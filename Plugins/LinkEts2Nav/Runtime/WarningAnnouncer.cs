using ETS2LA.Audio;
using LinkEts2Nav.Core;
using ETS2LA.Logging;

namespace LinkEts2Nav.Runtime;

/// <summary>Receives structured warnings so a future TTS renderer can describe the target.</summary>
public interface IWarningAnnouncer
{
    void Announce(CameraWarning warning);
}

/// <summary>Uses the host's bundled fixed WAV without interrupting other queued sounds.</summary>
public sealed class Ets2LaWarningAnnouncer : IWarningAnnouncer
{
    private readonly Action play;

    public Ets2LaWarningAnnouncer(Action? play = null)
        => this.play = play ?? (() => AudioHandler.Current.Play(DefaultAudio.PreAlert));

    public void Announce(CameraWarning warning)
    {
        try { play(); }
        catch (Exception ex)
        {
            Logger.Warn($"[[LinkEts2Nav]] Warning audio unavailable: {ex.GetType().Name}");
        }
    }
}
