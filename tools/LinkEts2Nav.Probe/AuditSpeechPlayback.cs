using System.Diagnostics;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Probe;

/// <summary>Completes prepared audio without a device, with the same playback coverage and withdrawals as driving.</summary>
internal sealed class AuditSpeechPlayback : IDisposable
{
    private sealed class SilentAudio : ISpeechSynthesizer, IGuidanceAudioPlayer
    {
        // The queue checks that prepared files exist. The silent player never opens the file.
        private static readonly string Marker = Environment.ProcessPath ?? throw new InvalidOperationException("Probe executable path unavailable.");
        public Task<string> SynthesizeAsync(string text, CancellationToken token) => Task.FromResult(Marker);
        public Task PlayAsync(string file, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    private readonly object gate = new();
    private readonly SpeechAnnouncementQueue cache;
    private readonly GuidancePlaybackManager playback;
    private readonly SpeechUsageCollector usage = new();
    private long completed;
    private bool hasRequests;
    public GuidanceCoordinator Guidance { get; }

    public AuditSpeechPlayback(Action<QueuedGuidanceSpeech> record)
    {
        var audio = new SilentAudio();
        cache = new(audio, _ => { });
        playback = new(cache, audio, () => null, usage: usage);
        Guidance = new(cache, playback);
        cache.CacheChanged += Wake;
        Guidance.Queued += Prepare;
        playback.Played += speech =>
        {
            lock (gate)
            {
                record(speech);
                completed++;
                Monitor.PulseAll(gate);
            }
        };
    }

    private void Prepare(QueuedGuidanceSpeech speech)
    {
        hasRequests = true;
        // Both wordings are ready before a trigger batch is released. Only the playback owner
        // chooses whether a follow-on can still be spoken, has ended, or was covered already.
        string[] texts = speech.FollowOn is { } follow ? [speech.Text, follow.StandaloneText] : [speech.Text];
        cache.UpdatePlan("audit-ready", texts, -20);
        WaitUntil(() => texts.All(cache.IsReady));
    }

    public void FinishBatch()
    {
        if (!hasRequests) return;
        // Pending alone drops the in-flight item before PlayAsync. A completion can also cover
        // and withdraw a second item. Wait for the owner's accepted work to be accounted for.
        WaitUntil(() => usage.Capture().Entries.Sum(e => e.EnqueuedCount - e.SupersededCount - e.WithdrawnCount) == completed);
        hasRequests = false;
    }

    private void Wake() { lock (gate) Monitor.PulseAll(gate); }

    private void WaitUntil(Func<bool> finished)
    {
        var elapsed = Stopwatch.StartNew();
        lock (gate)
        {
            while (!finished())
            {
                playback.ThrowIfFaulted();
                if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Silent playback did not finish its replay batch.");
                Monitor.Wait(gate, 100);
            }
        }
    }

    public void Dispose()
    {
        cache.CacheChanged -= Wake;
        try { playback.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally { cache.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
