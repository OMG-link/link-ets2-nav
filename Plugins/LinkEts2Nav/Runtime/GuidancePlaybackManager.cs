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
    public SpeechFollowOn? FollowOn {get;init;}
    public bool IsNotification { get; init; }
    public bool AllowsStalePosition {get;init;}
    public string? Stage {get;init;}
    public string? EventDescription {get;init;}
    public string SequenceId => SpeechSequenceId ?? EventId;
    public static QueuedGuidanceSpeech From(GuidanceTriggerResult result)
        =>new(result.Source,result.Event.Id,result.Trigger.Id,result.Trigger.SequenceOrder,result.Trigger.Priority,result.Trigger.Text)
            {SpeechSequenceId=result.Event.SequenceId,Stage=result.Trigger.Kind.ToString(),EventDescription=result.Event.Description.StartsWith("uncalibrated-proximity:",StringComparison.Ordinal)
                ?"uncalibrated-proximity":result.Event.Description,FollowOn=result.Trigger.FollowOn};
}

/// <summary>Owns pending playback, priority and same-event replacement. Preparation never plays.</summary>
public sealed class GuidancePlaybackManager:IAsyncDisposable
{
    private readonly object gate=new();
    private readonly SpeechPlaybackCoverage coverage=new();
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
    private readonly SpeechUsageCollector? usage;
    public event Action<QueuedGuidanceSpeech>? Played;
    public IReadOnlyList<QueuedGuidanceSpeech> Pending
    {get{lock(gate)return pending.Values.OrderBy(p=>p.Speech.Priority).ThenBy(p=>p.Order).Select(p=>p.Speech).ToArray();}}

    public GuidancePlaybackManager(SpeechAnnouncementQueue? cache,IGuidanceAudioPlayer player,
        Func<string?> fallback,Action<string>? log=null,CancellationToken cancellationToken=default,SpeechUsageCollector? usage=null)
    {
        this.usage=usage;
        this.cache=cache;this.player=player;this.fallback=fallback;this.log=log??(_=>{});
        stop=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if(cache is not null)cache.CacheChanged+=Wake;
        worker=Task.Run(RunAsync);
    }
    /// <summary>The owning driver observes unexpected playback or callback failures.</summary>
    public void ThrowIfFaulted()
    {
        cache?.ThrowIfFaulted();
        if(worker.IsCompleted)worker.GetAwaiter().GetResult();
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
                    coverage.End(result.Source,result.Event.SequenceId);
                    var key=(result.Source,result.Event.SequenceId);
                    if(pending.Remove(key,out var removed))usage?.Withdrawn(removed.Speech.Text);latest.Remove(key);
                }
                else if(result.Disposition==TriggerDisposition.Queued)
                {
                    EnqueueLocked(QueuedGuidanceSpeech.From(result));
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
            {IsNotification=true,AllowsStalePosition=notification.AllowsStalePosition,Stage="Notification"});
        PreparePending();Wake();
    }
    private void EnqueueLocked(QueuedGuidanceSpeech speech)
    {
        if(stop.IsCancellationRequested||speech.Text.Length==0||coverage.IsFinished(speech)||coverage.IsSpoken(speech.Source,speech.TriggerId))return;
        var key=(speech.Source,speech.SequenceId);
        if(latest.TryGetValue(key,out var previous)&&speech.SequenceOrder<previous.SequenceOrder)return;
        if(pending.TryGetValue(key,out var existing)&&existing.Speech.TriggerId==speech.TriggerId)
            pending[key]=(speech,existing.Order);
        else
        {
            if(previous is not null&&speech.SequenceOrder==previous.SequenceOrder)return;
            if(existing.Speech is not null)usage?.Superseded(existing.Speech.Text);
            pending[key]=(speech,++order);
            usage?.Enqueued(speech);
        }
        latest[key]=speech;
    }
    public void Refresh(string source,IReadOnlyList<SpeechTrigger> triggers)
    {
        lock(gate)
        {
            var texts=triggers.ToDictionary(t=>t.Id,StringComparer.Ordinal);
            foreach(var key in pending.Keys.Where(k=>k.Item1==source).ToArray())
            {
                var item=pending[key];
                if(texts.TryGetValue(item.Speech.TriggerId,out var point)&&point.Text.Length>0)
                {
                    if(item.Speech.Text!=point.Text)usage?.Reworded(point.Text);
                    pending[key]=(item.Speech with{Text=point.Text,FollowOn=point.FollowOn},item.Order);
                }
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
            coverage.Withdraw(source,ids);
            foreach(var key in pending.Where(p=>p.Key.Item1==source&&ids.Contains(p.Value.Speech.EventId)).Select(p=>p.Key).ToArray())
            {usage?.Withdrawn(pending[key].Speech.Text);pending.Remove(key);}
            foreach(var key in latest.Where(p=>p.Key.Item1==source&&ids.Contains(p.Value.EventId)).Select(p=>p.Key).ToArray())latest.Remove(key);
        }
        PreparePending();Wake();
    }
    public void Reset()
    {
        lock(gate){foreach(var item in pending.Values)usage?.Withdrawn(item.Speech.Text);pending.Clear();latest.Clear();coverage.Reset();generation++;updateHeld=false;}
        PreparePending();Wake();
    }
    private void PreparePending()
    {
        string[] texts;
        lock(gate)texts=pending.Values.Select(p=>coverage.Resolve(p.Speech).Text).ToArray();
        cache?.UpdatePlan("guidance-playback",texts,-10);
    }
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
                    (QueuedGuidanceSpeech Speech,long Generation,string File,SpeechPlaybackOrigin Origin)? next=null;
                    lock(gate)
                    {
                        if(!active&&!positionIndependentPlayback)break;
                        foreach(var item in pending.Values.Where(p=>(active||positionIndependentPlayback&&p.Speech.IsNotification&&p.Speech.AllowsStalePosition)
                            &&(!updateHeld||p.Speech.IsNotification)).OrderBy(p=>p.Speech.Priority).ThenBy(p=>p.Order))
                        {
                            var speech=coverage.Resolve(item.Speech);
                            string? file=null;var origin=SpeechPlaybackOrigin.Fallback;
                            if(cache?.TryGetAudio(speech.Text,out var ready,out bool exact)==true)
                            {file=ready;origin=exact?SpeechPlaybackOrigin.ExactPack:SpeechPlaybackOrigin.Prepared;}
                            else if(cache is null || cache.IsUnavailable(speech.Text))
                            {
                                file=fallback();
                            }
                            if(file is null)continue;
                            next=(speech,generation,file,origin);break;
                        }
                        if(next is {} selected)pending.Remove((selected.Speech.Source,selected.Speech.SequenceId));
                    }
                    if(next is not {} job)break;
                    PreparePending();
                    try
                    {
                        await player.PlayAsync(job.File,stop.Token).ConfigureAwait(false);
                    }
                    catch(TimeoutException ex)
                    {
                        // The host does not report backend errors through its
                        // completion callback. A missing callback times out in
                        // Ets2LaGuidanceAudioPlayer; device recovery can retry it.
                        usage?.PlaybackFailed(job.Speech.Text);
                        log("语音播放超时，保留待播项："+ex.Message);
                        lock(gate)
                        {
                            var key=(job.Speech.Source,job.Speech.SequenceId);
                            if(job.Generation==generation&&!pending.ContainsKey(key)&&latest.TryGetValue(key,out var newest)&&newest.TriggerId==job.Speech.TriggerId)
                                pending[key]=(job.Speech,++order);
                        }
                        await Task.Delay(500,stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    usage?.Played(job.Speech.Text,job.Origin);
                    bool current;
                    lock(gate)
                    {
                        current=job.Generation==generation;
                        if(current&&job.Origin!=SpeechPlaybackOrigin.Fallback)
                        {
                            // Confirm and remove under the selection gate, before the worker can start another cue.
                            coverage.Confirm(job.Speech);
                            if(job.Speech.FollowOn is {} follow)
                            {
                                var key=(job.Speech.Source,follow.SequenceId);
                                if(pending.TryGetValue(key,out var covered)&&covered.Speech.TriggerId==follow.TriggerId)
                                {pending.Remove(key);usage?.Withdrawn(covered.Speech.Text);}
                            }
                        }
                    }
                    if(current)
                    {
                        log("语音播放完成："+job.Speech.Text+" 事件="+job.Speech.EventId);
                        Played?.Invoke(job.Speech);
                    }
                }
            }
        }
        catch(OperationCanceledException error)when(stop.IsCancellationRequested&&error.CancellationToken==stop.Token){}
    }
    public async ValueTask DisposeAsync()
    {
        if(cache is not null)cache.CacheChanged-=Wake;
        try{stop.Cancel();}
        finally
        {
            changed.Writer.TryComplete();
            try{await worker.ConfigureAwait(false);}
            finally
            {
                try{cache?.ClearPlan("guidance-playback");}
                finally{stop.Dispose();}
            }
        }
    }
}
