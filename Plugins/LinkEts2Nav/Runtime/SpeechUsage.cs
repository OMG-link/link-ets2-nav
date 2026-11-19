using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Runtime;

public sealed record SpeechUsageContext(string Source,string Stage,string EventDescription);
public sealed record SpeechUsageEntry(string Text,DateTimeOffset FirstSeenAt,DateTimeOffset LastSeenAt)
{
    public long EnqueuedCount {get;init;}
    public long PlayedCount {get;init;}
    public long FallbackCount {get;init;}
    public long PlaybackFailureCount {get;init;}
    public long SupersededCount {get;init;}
    public long WithdrawnCount {get;init;}
    public long RewordedCount {get;init;}
    public long ExactPackPlayedCount {get;init;}
    public long PreparationCount {get;init;}
    public long PreparationFailureCount {get;init;}
    public long PreparationCancelledCount {get;init;}
    public long WorkerCacheHitCount {get;init;}
    public long WorkerCacheMissCount {get;init;}
    public long WorkerCacheUnknownCount {get;init;}
    public double PreparationMilliseconds {get;init;}
    public double MaximumPreparationMilliseconds {get;init;}
    public bool CurrentlyPreGenerated {get;init;}
    public IReadOnlyList<SpeechUsageContext> Contexts {get;init;}=[];
}
public sealed record SpeechUsageSnapshot(int Schema,int MetricVersion,string CollectionSessionId,long CollectionSessions,
    DateTimeOffset CollectionStartedAt,DateTimeOffset CapturedAt,IReadOnlyList<SpeechUsageEntry> Entries);
public enum SpeechPlaybackOrigin { Prepared,ExactPack,Fallback }

/// <summary>Exact text counters only. Callbacks never read files, synthesize, or play audio.</summary>
public sealed class SpeechUsageCollector
{
    private readonly object gate=new();
    private readonly Dictionary<string,SpeechUsageEntry> entries=new(StringComparer.Ordinal);
    private HashSet<string> exactPack=new(StringComparer.Ordinal);
    private readonly string session=Guid.NewGuid().ToString("N");
    private readonly long sessions;
    private readonly DateTimeOffset started;
    private long revision=1;
    public SpeechUsageCollector(SpeechUsageSnapshot? previous=null)
    {
        if(previous is not null)
        {
            if(previous.Schema!=1||previous.MetricVersion!=1)
                throw new NotSupportedException("Unsupported speech usage statistics version.");
            if(previous.Entries is null||previous.CollectionSessions<1)
                throw new InvalidDataException("Invalid speech usage statistics.");
            foreach(var entry in previous.Entries)
                if(entry is null||string.IsNullOrWhiteSpace(entry.Text)||entry.Contexts is null||entry.Contexts.Any(c=>c is null)
                    ||entry.EnqueuedCount<0||entry.PlayedCount<0||entry.FallbackCount<0||entry.PreparationCount<0
                    ||!double.IsFinite(entry.PreparationMilliseconds)||entry.PreparationMilliseconds<0
                    ||!double.IsFinite(entry.MaximumPreparationMilliseconds)||entry.MaximumPreparationMilliseconds<0
                    ||!entries.TryAdd(entry.Text,entry))
                    throw new InvalidDataException("Invalid or duplicate speech usage text.");
        }
        sessions=previous?.CollectionSessions==long.MaxValue?long.MaxValue:(previous?.CollectionSessions??0)+1;
        started=previous?.CollectionStartedAt??DateTimeOffset.UtcNow;
    }
    public long Revision {get{lock(gate)return revision;}}
    public void SetExactPack(IEnumerable<string> texts)
    {lock(gate){exactPack=texts.ToHashSet(StringComparer.Ordinal);revision++;}}
    public void Enqueued(QueuedGuidanceSpeech speech)=>Change(speech.Text,e=>e with
    {
        EnqueuedCount=e.EnqueuedCount+1,
        Contexts=e.Contexts.Append(new(speech.Source,speech.Stage??(speech.IsNotification?"Notification":"Unknown"),speech.EventDescription??""))
            .Distinct().OrderBy(c=>c.Source,StringComparer.Ordinal).ThenBy(c=>c.Stage,StringComparer.Ordinal).ThenBy(c=>c.EventDescription,StringComparer.Ordinal).ToArray()
    });
    public void Played(string text,SpeechPlaybackOrigin origin)=>Change(text,e=>origin==SpeechPlaybackOrigin.Fallback
        ?e with{FallbackCount=e.FallbackCount+1}
        :e with{PlayedCount=e.PlayedCount+1,ExactPackPlayedCount=e.ExactPackPlayedCount+(origin==SpeechPlaybackOrigin.ExactPack?1:0)});
    public void PlaybackFailed(string text)=>Change(text,e=>e with{PlaybackFailureCount=e.PlaybackFailureCount+1});
    public void Superseded(string text)=>Change(text,e=>e with{SupersededCount=e.SupersededCount+1});
    public void Withdrawn(string text)=>Change(text,e=>e with{WithdrawnCount=e.WithdrawnCount+1});
    public void Reworded(string text)=>Change(text,e=>e with{RewordedCount=e.RewordedCount+1});
    public void Prepared(string text,double milliseconds,bool? cacheHit,bool failed=false,bool cancelled=false)
    {
        if(!double.IsFinite(milliseconds)||milliseconds<0)return;
        Change(text,e=>e with
        {
            PreparationCount=e.PreparationCount+1,PreparationFailureCount=e.PreparationFailureCount+(failed?1:0),
            PreparationCancelledCount=e.PreparationCancelledCount+(cancelled?1:0),
            PreparationMilliseconds=e.PreparationMilliseconds+milliseconds,MaximumPreparationMilliseconds=Math.Max(e.MaximumPreparationMilliseconds,milliseconds),
            WorkerCacheHitCount=e.WorkerCacheHitCount+(cacheHit==true?1:0),WorkerCacheMissCount=e.WorkerCacheMissCount+(cacheHit==false?1:0),
            WorkerCacheUnknownCount=e.WorkerCacheUnknownCount+(cacheHit is null?1:0)
        });
    }
    private void Change(string text,Func<SpeechUsageEntry,SpeechUsageEntry> update)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        lock(gate)
        {
            var now=DateTimeOffset.UtcNow;
            entries[text]=update(entries.GetValueOrDefault(text)??new(text,now,now)) with{LastSeenAt=now};revision++;
        }
    }
    public SpeechUsageSnapshot Capture()
    {
        SpeechUsageEntry[] copy;HashSet<string> currentPack;
        lock(gate){copy=entries.Values.ToArray();currentPack=new(exactPack,StringComparer.Ordinal);}
        return new(1,1,session,sessions,started,DateTimeOffset.UtcNow,copy
            .Select(e=>e with{CurrentlyPreGenerated=currentPack.Contains(e.Text)}).OrderByDescending(e=>e.PlayedCount)
            .ThenByDescending(e=>e.PreparationMilliseconds).ThenBy(e=>e.Text,StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Periodic full checkpoints prevent repeated flushes or restarts from double-counting a snapshot.</summary>
public sealed class SpeechUsageStore:IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private readonly string directory;
    private readonly Action<string> log;
    private readonly SemaphoreSlim writes=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private readonly Task worker;
    private long savedRevision;
    private bool primaryValid;
    public SpeechUsageCollector Collector {get;}
    public string? LastError {get;private set;}
    public SpeechUsageStore(string directory,Action<string>? log=null,Action<string>? warning=null)
    {
        this.directory=directory;this.log=log??(_=>{});
        SpeechUsageSnapshot? previous=null;SpeechUsageCollector? replacement=null;
        foreach(string file in new[]{"summary.json","summary.previous.json"})
        {
            string contents;
            try { contents=File.ReadAllText(Path.Combine(directory,file)); }
            catch(FileNotFoundException) { continue; }
            catch(DirectoryNotFoundException) { continue; }
            try
            {
                previous=JsonSerializer.Deserialize<SpeechUsageSnapshot>(contents)??throw new InvalidDataException("Empty statistics.");
                _=new SpeechUsageCollector(previous);primaryValid=file=="summary.json";
            }
            catch(Exception error)when(file=="summary.json"&&(error is JsonException or InvalidDataException))
            {
                replacement=new SpeechUsageCollector();
                string backup=ReplaceCorruptPrimary(Path.Combine(directory,file),replacement.Capture());
                primaryValid=true;savedRevision=replacement.Revision;
                warning?.Invoke("\u8bed\u97f3\u7edf\u8ba1\u4e3b\u6863\u635f\u574f\uff0c\u5df2\u539f\u6837\u4fdd\u5b58\u81f3 "+backup+"\uff0c\u5e76\u521b\u5efa\u7a7a\u7edf\u8ba1\uff1a"+error);
            }
            break;
        }
        Collector=replacement??new(previous);worker=Task.Run(CheckpointLoop);
    }
    private static string ReplaceCorruptPrimary(string primary,SpeechUsageSnapshot empty)
    {
        string suffix=DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N");
        string backup=Path.Combine(Path.GetDirectoryName(primary)!,"summary.unreadable-"+suffix+".json");
        string temporary=primary+"."+suffix+".tmp";
        // Prepare the complete replacement before moving the original. Neither
        // move overwrites another file; every failure leaves the original bytes
        // either at their initial path or at the unique backup path.
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                JsonSerializer.Serialize(stream,empty,Json);
            File.Move(primary,backup);
            File.Move(temporary,primary);
            return backup;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    /// <summary>The owning driver must poll this to observe unknown checkpoint-worker failures.</summary>
    public void ThrowIfFaulted()
    {if(worker.IsCompleted)worker.GetAwaiter().GetResult();}
    private async Task CheckpointLoop()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        try {while(await timer.WaitForNextTickAsync(stop.Token))await FlushAsync();}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    }
    public async Task FlushAsync()
    {
        await writes.WaitAsync();
        try
        {
            long version=Collector.Revision;if(version==savedRevision)return;
            // Serialization errors are internal/data failures, not a reason to
            // retry storage or replace history. Validate before opening a file.
            string contents=JsonSerializer.Serialize(Collector.Capture(),Json);
            Directory.CreateDirectory(directory);
            string target=Path.Combine(directory,"summary.json"),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary,contents,new UTF8Encoding(false));
                if(primaryValid&&File.Exists(target))File.Copy(target,Path.Combine(directory,"summary.previous.json"),true);
                else if(File.Exists(target))File.Copy(target,Path.Combine(directory,"summary.unreadable-"+
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]+".json"));
                File.Move(temporary,target,true);primaryValid=true;savedRevision=version;LastError=null;
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
        {Error("保存语音统计失败",ex);}
        finally{writes.Release();}
    }
    public async Task<string> ExportAsync()
    {
        var snapshot=Collector.Capture();
        string output=Path.Combine(directory,"Exports",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"speech-usage.json"),JsonSerializer.Serialize(snapshot,Json),new UTF8Encoding(false));
        var csv=new StringBuilder("Text,Enqueued,Played,Fallback,PlaybackFailures,Superseded,Withdrawn,Reworded,CurrentlyPreGenerated,ExactPackPlayed,PreparationRequests,PreparationFailures,PreparationCancelled,PreparationMilliseconds,MaximumPreparationMilliseconds,WorkerCacheHits,WorkerCacheMisses,WorkerCacheUnknown,FirstSeenAt,LastSeenAt,Contexts\r\n");
        foreach(var e in snapshot.Entries)
            csv.AppendLine(string.Join(",",new object?[]{e.Text,e.EnqueuedCount,e.PlayedCount,e.FallbackCount,e.PlaybackFailureCount,e.SupersededCount,
                e.WithdrawnCount,e.RewordedCount,e.CurrentlyPreGenerated,e.ExactPackPlayedCount,e.PreparationCount,e.PreparationFailureCount,e.PreparationCancelledCount,
                e.PreparationMilliseconds,e.MaximumPreparationMilliseconds,e.WorkerCacheHitCount,e.WorkerCacheMissCount,e.WorkerCacheUnknownCount,
                e.FirstSeenAt.ToString("O"),e.LastSeenAt.ToString("O"),JsonSerializer.Serialize(e.Contexts)}.Select(Csv)));
        await File.WriteAllTextAsync(Path.Combine(output,"speech-usage.csv"),csv.ToString(),new UTF8Encoding(true));
        return output;
    }
    private static string Csv(object? value)
    {
        string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        // Quoting alone does not stop spreadsheet formula evaluation.
        if(text.Length>0&&"=+-@\t\r".Contains(text[0]))text="'"+text;
        return "\""+text.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
    }
    private void Error(string action,Exception ex)
    {LastError=action+"："+ex;log(LastError);}
    public async ValueTask DisposeAsync()
    {stop.Cancel();try{await worker;await FlushAsync();}finally{stop.Dispose();}}
}

public static class SpeechUsageReports
{
    private static SpeechUsageStore? current;
    public static SpeechUsageStore? Current=>Volatile.Read(ref current);
    public static void Attach(SpeechUsageStore store)=>Volatile.Write(ref current,store);
}
