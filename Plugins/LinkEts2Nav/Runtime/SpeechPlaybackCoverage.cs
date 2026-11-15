using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Runtime;

/// <summary>Only the playback owner's gate may access this ledger. Queueing never confirms spoken content.</summary>
internal sealed class SpeechPlaybackCoverage
{
    private readonly HashSet<(string Source,string Trigger)> spoken=[];
    private readonly HashSet<(string Source,string Sequence)> ended=[];
    private readonly HashSet<(string Source,string Event)> withdrawn=[];
    public bool IsSpoken(string source,string trigger)=>spoken.Contains((source,trigger));
    public bool IsFinished(QueuedGuidanceSpeech speech)=>ended.Contains((speech.Source,speech.SequenceId));
    public void End(string source,string sequence)=>ended.Add((source,sequence));
    public void Withdraw(string source,IEnumerable<string> events)
    {foreach(var id in events)withdrawn.Add((source,id));}
    public QueuedGuidanceSpeech Resolve(QueuedGuidanceSpeech speech)
    {
        if(speech.FollowOn is not {} follow)return speech;
        bool unavailable=IsSpoken(speech.Source,follow.TriggerId)||ended.Contains((speech.Source,follow.SequenceId))
            ||withdrawn.Contains((speech.Source,follow.EventId));
        return unavailable?speech with{Text=follow.StandaloneText,FollowOn=null}:speech;
    }
    public void Confirm(QueuedGuidanceSpeech speech)
    {
        spoken.Add((speech.Source,speech.TriggerId));
        if(speech.FollowOn is {} follow)spoken.Add((speech.Source,follow.TriggerId));
    }
    public void Reset(){spoken.Clear();ended.Clear();withdrawn.Clear();}
}
