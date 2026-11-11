using ETS2LA.Audio;
namespace LinkEts2Nav.Runtime;

/// <summary>Uses the host's public one-shot loop callback to await real playback completion.</summary>
public sealed class Ets2LaGuidanceAudioPlayer:IGuidanceAudioPlayer
{
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
