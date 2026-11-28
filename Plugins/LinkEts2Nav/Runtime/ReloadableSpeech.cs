namespace LinkEts2Nav.Runtime;

/// <summary>Swaps only speech resources; map, route progress and trigger consumption remain intact.</summary>
public sealed class ReloadableSpeech:ISpeechSynthesizer,ISpeechSynthesisDiagnostics
{
    private sealed class Generation(ISpeechSynthesizer worker)
    {
        public ISpeechSynthesizer Worker {get;}=worker;
        public CancellationTokenSource Stop {get;}=new();
        public TaskCompletionSource Drained {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile Task<Generation>? Replacement;
        public Task? Retirement;public int Active;public bool Retiring;
    }
    public bool? LastCacheHit {get{lock(gate)return(worker.Worker as ISpeechSynthesisDiagnostics)?.LastCacheHit;}}
    private readonly SpeechOptions options;
    private readonly SpeechPackSettings settings;
    private readonly Func<SpeechOptions,ISpeechSynthesizer> create;
    private readonly object gate=new();
    private Generation worker;
    private Task<Generation>? transition;
    private bool closed;
    private long revision;
    public HashSet<string> Names {get;}
    public IReadOnlyDictionary<string,string> Fixed {get;}
    public ReloadableSpeech(SpeechOptions options,SpeechPackSettings settings,Action<string>? log=null,
        Func<SpeechOptions,ISpeechSynthesizer>? create=null)
    {
        this.options=options;this.settings=settings;this.create=create??(value=>new KokoroSpeechProcess(value));
        worker=new(this.create(options));
        Fixed=PreGeneratedSpeech.Load(options.PackDirectory,log);
        Names=PreGeneratedSpeech.Load(Path.Combine(options.PackDirectory,"CityNames"),log,4096).Keys.ToHashSet(StringComparer.Ordinal);
        revision=settings.Pending is {} saved&&Path.GetFullPath(saved.Directory)==Path.GetFullPath(options.PackDirectory)?saved.Revision:0;
        settings.MarkActive(options.PackDirectory);
    }
    public async Task<string> SynthesizeAsync(string text,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            Generation selected;Task<Generation>? replacement;
            lock(gate)
            {
                ObjectDisposedException.ThrowIf(closed,this);
                selected=worker;replacement=selected.Replacement;
                if(replacement is null)selected.Active++;
            }
            if(replacement is not null){await replacement.WaitAsync(token).ConfigureAwait(false);continue;}
            CancellationTokenSource? request=null;
            try
            {
                request=CancellationTokenSource.CreateLinkedTokenSource(token,selected.Stop.Token);
                string file=await selected.Worker.SynthesizeAsync(text,request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                return file;
            }
            catch(OperationCanceledException) when(selected.Stop.IsCancellationRequested&&!token.IsCancellationRequested&&selected.Replacement is not null)
            {replacement=selected.Replacement;}
            finally
            {
                try{request?.Dispose();}
                finally
                {
                    lock(gate)
                        if(--selected.Active==0&&selected.Retiring)selected.Drained.TrySetResult();
                }
            }
            await replacement!.WaitAsync(token).ConfigureAwait(false);
        }
    }
    public async Task<bool> ApplyPendingAsync(SpeechAnnouncementQueue queue)
    {
        while(true)
        {
            Task<Generation> operation;TaskCompletionSource? start=null;
            lock(gate)
            {
                ObjectDisposedException.ThrowIf(closed,this);
                if(transition is {} pending)operation=pending;
                else
                {
                    if(settings.Pending is not {} selection||selection.Revision<=revision)return false;
                    start=new(TaskCreationOptions.RunContinuationsAsynchronously);
                    operation=SwitchAsync(worker,selection,queue,start.Task);
                    worker.Replacement=operation;transition=operation;
                }
            }
            // Publish the replacement task before any old request is canceled.
            start?.SetResult();
            await operation.ConfigureAwait(false);
            lock(gate)if(ReferenceEquals(transition,operation))transition=null;
            if(start is not null)return true;
        }
    }
    private async Task<Generation> SwitchAsync(Generation previous,SpeechPackSelection selection,SpeechAnnouncementQueue queue,Task start)
    {
        await start.ConfigureAwait(false);
        await Retire(previous).ConfigureAwait(false);
        var next=new Generation(create(new SpeechOptions{Enabled=options.Enabled,PreparedSpeechOnly=options.PreparedSpeechOnly,ModelDirectory=options.ModelDirectory,
            WorkerPath=options.WorkerPath,CacheDirectory=options.CacheDirectory,Voice=options.Voice,
            Threads=options.Threads,PackDirectory=selection.Directory}));
        bool installed=false;
        try
        {
            queue.ReplacePreparedAudio(selection.Fixed,()=>{lock(gate){worker=next;installed=true;}});
            Names.Clear();Names.UnionWith(selection.Names.Keys);
            revision=selection.Revision;settings.MarkActive(selection.Directory);
            return next;
        }
        finally{if(!installed)await Retire(next).ConfigureAwait(false);}
    }
    private Task Retire(Generation generation)
    {
        lock(gate)
        {
            if(generation.Retirement is {} retirement)return retirement;
            generation.Retiring=true;
            if(generation.Active==0)generation.Drained.TrySetResult();
            return generation.Retirement=Task.Run(async()=>
            {
                // Cancellation callbacks run without the state gate held.
                try{generation.Stop.Cancel();}
                finally
                {
                    try{await generation.Drained.Task.ConfigureAwait(false);}
                    finally
                    {
                        try{await generation.Worker.DisposeAsync().ConfigureAwait(false);}
                        finally{generation.Stop.Dispose();}
                    }
                }
            });
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task<Generation>? pending;
        lock(gate){closed=true;pending=transition;}
        try{if(pending is not null)await pending.ConfigureAwait(false);}
        finally
        {
            Generation current;lock(gate)current=worker;
            try{await Retire(current).ConfigureAwait(false);}
            finally{settings.MarkActive(null);}
        }
    }
}
