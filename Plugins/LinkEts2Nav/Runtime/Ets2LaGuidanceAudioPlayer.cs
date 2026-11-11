using ETS2LA.Audio;
namespace LinkEts2Nav.Runtime;

/// <summary>Uses the host's public one-shot loop callback to await real playback completion.</summary>
public sealed class Ets2LaGuidanceAudioPlayer:IGuidanceAudioPlayer
{
    public static string FallbackFile(string hostDirectory,Action<string>? log=null,bool allowCustom=true)
    {
        try
        {
            // A custom recording may contain a fixed distance; synthesized speech failures use a neutral tone.
            string? custom=allowCustom?WarningAudioOptions.LoadFilePath(hostDirectory):null;
            if(custom is not null&&File.Exists(custom))return custom;
        }
        catch(Exception ex){log?.Invoke("预警音频配置不可用，使用内置提示音："+ex.Message);}
        return Path.Combine(hostDirectory,"Assets","Sounds","pre_alert.wav");
    }
    public async Task PlayAsync(string file,CancellationToken cancellationToken)
    {
        if(!File.Exists(file))throw new FileNotFoundException("Guidance audio file is missing.",file);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls=0;
        AudioHandler.Current.Queue(file,()=>
        {
            if(cancellationToken.IsCancellationRequested){done.TrySetCanceled(cancellationToken);return false;}
            if(Interlocked.Increment(ref calls)==1)return true;
            done.TrySetResult();return false;
        });
        // A backend exception does not invoke the next loop callback. Do not hang forever.
        await done.Task.WaitAsync(TimeSpan.FromMinutes(2),cancellationToken).ConfigureAwait(false);
    }
}
