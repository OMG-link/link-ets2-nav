using System.Threading.Channels;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Runtime;

public interface IGuidanceAudioPlayer
{
    Task PlayAsync(string file,CancellationToken cancellationToken);
}

public sealed record QueuedGuidanceSpeech(string Source,string EventId,string TriggerId,
    double SequenceOrder,int Priority,string Text)
{
    public string? SpeechSequenceId { get; init; }
    public bool IsNotification { get; init; }
    public bool AllowsStalePosition {get;init;}
    public string SequenceId => SpeechSequenceId ?? EventId;
}

/// <summary>Owns pending playback, priority and same-event replacement. Preparation never plays.</summary>
public sealed class GuidancePlaybackManager:IAsyncDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<(string,string),(QueuedGuidanceSpeech Speech,long Order)> pending=[];
    private readonly Dictionary<(string,string),QueuedGuidanceSpeech> latest=[];
    private readonly Channel<bool> changed=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropWrite,SingleReader=true});
    private readonly CancellationTokenSource stop;
    private readonly SpeechAnnouncementQueue? cache;
    private readonly IGuidanceAudioPlayer player;
    private readonly Func<string?> fallback;
    private readonly Action<string> log;
    private readonly Task worker;
    private long order,generation;
    private bool active=true,updateHeld;
    private bool positionIndependentPlayback;
    public event Action<QueuedGuidanceSpeech>? Played;
    public IReadOnlyList<QueuedGuidanceSpeech> Pending
    {get{lock(gate)return pending.Values.OrderBy(p=>p.Speech.Priority).ThenBy(p=>p.Order).Select(p=>p.Speech).ToArray();}}

    public GuidancePlaybackManager(SpeechAnnouncementQueue? cache,IGuidanceAudioPlayer player,
        Func<string?> fallback,Action<string>? log=null,CancellationToken cancellationToken=default)
    {
        this.cache=cache;this.player=player;this.fallback=fallback;this.log=log??(_=>{});
        stop=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if(cache is not null)cache.CacheChanged+=Wake;
        worker=Task.Run(RunAsync);
    }
    public void SetActive(bool value){lock(gate)active=value;Wake();}
    public void SetPositionIndependentPlayback(bool value){lock(gate)positionIndependentPlayback=value;Wake();}
    /// <summary>Prevent the playback worker from starting a stale item while a sample is being planned.</summary>
    public void BeginUpdate(){lock(gate)updateHeld=true;}
    public void ApplyBatch(IReadOnlyList<GuidanceTriggerResult> results,bool isActive)
    {
        lock(gate)
        {
            foreach(var result in results)
            {
                if(result.Disposition==TriggerDisposition.Ended)
                {
                    var key=(result.Source,result.Event.SequenceId);
                    pending.Remove(key);latest.Remove(key);
                }
                else if(result.Disposition==TriggerDisposition.Queued)
                {
                    var t=result.Trigger;
                    EnqueueLocked(new(result.Source,result.Event.Id,t.Id,t.SequenceOrder,t.Priority,t.Text)
                        {SpeechSequenceId=result.Event.SequenceId});
                }
            }
            active=isActive;updateHeld=false;
        }
        PreparePending();Wake();
    }
    /// <summary>One-shot notifications have no fabricated route end or trigger point.</summary>
    public void Notify(string source,GuidanceNotification notification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Text);
        string id="notification:"+notification.Id;
        lock(gate)EnqueueLocked(new(source,id,id,0,notification.Priority,notification.Text)
            {IsNotification=true,AllowsStalePosition=notification.AllowsStalePosition});
        PreparePending();Wake();
    }
    private void EnqueueLocked(QueuedGuidanceSpeech speech)
    {
        if(stop.IsCancellationRequested||speech.Text.Length==0)return;
        var key=(speech.Source,speech.SequenceId);
        if(latest.TryGetValue(key,out var previous)&&speech.SequenceOrder<previous.SequenceOrder)return;
        if(pending.TryGetValue(key,out var existing)&&existing.Speech.TriggerId==speech.TriggerId)
            pending[key]=(speech,existing.Order);
        else
        {
            if(previous is not null&&speech.SequenceOrder==previous.SequenceOrder)return;
            pending[key]=(speech,++order);
        }
        latest[key]=speech;
    }
    public void Refresh(string source,IReadOnlyList<SpeechTrigger> triggers)
    {
        lock(gate)
        {
            var texts=triggers.ToDictionary(t=>t.Id,t=>t.Text,StringComparer.Ordinal);
            foreach(var key in pending.Keys.Where(k=>k.Item1==source).ToArray())
            {
                var item=pending[key];
                if(texts.TryGetValue(item.Speech.TriggerId,out var text)&&text.Length>0)
                    pending[key]=(item.Speech with{Text=text},item.Order);
            }
        }
        PreparePending();Wake();
    }
    // Route replacement explicitly withdraws affected events; normal map windows do not erase queued audio.
    public void Withdraw(string source,IEnumerable<string> eventIds)
    {
        var ids=eventIds.ToHashSet(StringComparer.Ordinal);
        lock(gate)
        {
            foreach(var key in pending.Where(p=>p.Key.Item1==source&&ids.Contains(p.Value.Speech.EventId)).Select(p=>p.Key).ToArray())pending.Remove(key);
            foreach(var key in latest.Where(p=>p.Key.Item1==source&&ids.Contains(p.Value.EventId)).Select(p=>p.Key).ToArray())latest.Remove(key);
        }
        PreparePending();Wake();
    }
    public void Reset()
    {
        lock(gate){pending.Clear();latest.Clear();generation++;updateHeld=false;}
        PreparePending();Wake();
    }
    private void PreparePending()=>cache?.UpdatePlan("guidance-playback",Pending.Select(p=>p.Text).ToArray(),-10);
    private void Wake()=>changed.Writer.TryWrite(true);
    private async Task RunAsync()
    {
        try
        {
            while(await changed.Reader.WaitToReadAsync(stop.Token))
            {
                while(changed.Reader.TryRead(out _)){}
                while(!stop.IsCancellationRequested)
                {
                    (QueuedGuidanceSpeech Speech,long Generation,string File)? next=null;
                    lock(gate)
                    {
                        if(!active&&!positionIndependentPlayback)break;
                        foreach(var item in pending.Values.Where(p=>(active||positionIndependentPlayback&&p.Speech.IsNotification&&p.Speech.AllowsStalePosition)
                            &&(!updateHeld||p.Speech.IsNotification)).OrderBy(p=>p.Speech.Priority).ThenBy(p=>p.Order))
                        {
                            string? file=null;
                            if(cache?.TryGetAudio(item.Speech.Text,out var ready)==true)file=ready;
                            else if(cache is null||cache.HasFailed(item.Speech.Text))
                            {
                                try{file=fallback();}
                                catch(Exception ex){log("回退音频配置不可用，保留待播项："+ex.Message);}
                            }
                            if(file is null)continue;
                            next=(item.Speech,generation,file);break;
                        }
                        if(next is {} selected)pending.Remove((selected.Speech.Source,selected.Speech.SequenceId));
                    }
                    if(next is not {} job)break;
                    PreparePending();
                    try
                    {
                        await player.PlayAsync(job.File,stop.Token).ConfigureAwait(false);
                        bool current;lock(gate)current=job.Generation==generation;
                        if(current)
                        {
                            log("语音播放完成："+job.Speech.Text+" 事件="+job.Speech.EventId);
                            Played?.Invoke(job.Speech);
                        }
                    }
                    catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
                    catch(Exception ex)
                    {
                        log("语音播放失败，保留待播项："+ex.Message);
                        lock(gate)
                        {
                            var key=(job.Speech.Source,job.Speech.SequenceId);
                            if(job.Generation==generation&&!pending.ContainsKey(key)&&latest.TryGetValue(key,out var newest)&&newest.TriggerId==job.Speech.TriggerId)
                                pending[key]=(job.Speech,++order);
                        }
                        await Task.Delay(500,stop.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync()
    {
        if(cache is not null)cache.CacheChanged-=Wake;
        stop.Cancel();changed.Writer.TryComplete();await worker.ConfigureAwait(false);
        cache?.ClearPlan("guidance-playback");stop.Dispose();
    }
}
