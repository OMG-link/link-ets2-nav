namespace LinkEts2Nav.Runtime;

/// <summary>
/// The native plugin toggles fined before writing its payload. Confirm a changed sample on a
/// following identical read, retaining the pose/time at first detection. Call under the store lock.
/// Shared memory is not an event journal: multiple events between reads can still be lost.
/// </summary>
internal sealed class FineObservationTracker
{
    private const int Capacity = 64;
    private readonly Queue<FineObservation> history = new();
    private readonly Queue<FineObservation> undrained = new();
    private RawSample? previous;
    private PendingSample? pending;
    private long sequence;
    public long DroppedCount { get; private set; }
    public FineObservation? Latest { get; private set; }
    public FineObservation? LatestCamera { get; private set; }

    public void Observe(bool flag, string offence, ulong amount, VehicleSnapshot? pose, DateTimeOffset observedAt)
    {
        var raw = new RawSample(flag, offence, amount);
        if (previous is null)
        {
            // Connecting while the native toggle is true does not identify a new event.
            previous = raw;
            return;
        }
        if (pending is { } candidate)
        {
            if (candidate.Raw.Flag != flag)
            {
                // A second edge arrived before the first payload could be confirmed.
                // Its old payload cannot safely be reconstructed; start from this edge.
                pending = pose is null ? null : new(raw, pose, observedAt, FineObservationSignal.Toggle);
            }
            else if (candidate.Raw != raw)
            {
                // The event payload followed its toggle. Keep the first pose and await stability.
                pending = candidate with { Raw = raw };
            }
            else
            {
                Commit(candidate);
                pending = null;
            }
        }
        else if (previous != raw && pose is not null)
        {
            var signal = previous.Flag != flag ? FineObservationSignal.Toggle : FineObservationSignal.PayloadChange;
            pending = new(raw, pose, observedAt, signal);
        }
        previous = raw;
    }

    private void Commit(PendingSample candidate)
    {
        var observation = new FineObservation(candidate.Raw.Offence, candidate.Raw.Amount,
            candidate.Pose, candidate.ObservedAt)
        {
            EventFlag = candidate.Raw.Flag,
            Sequence = ++sequence,
            Signal = candidate.Signal
        };
        Latest = observation;
        if (observation.IsSpeedCameraOffence) LatestCamera = observation;
        if (history.Count == Capacity) history.Dequeue();
        history.Enqueue(observation);
        if (undrained.Count == Capacity) { undrained.Dequeue(); DroppedCount++; }
        undrained.Enqueue(observation);
    }

    public IReadOnlyList<FineObservation> GetHistory() => Array.AsReadOnly(history.ToArray());

    public IReadOnlyList<FineObservation> Drain()
    {
        var result = Array.AsReadOnly(undrained.ToArray());
        undrained.Clear();
        return result;
    }

    public void ResetSession()
    {
        previous = null;
        pending = null;
        history.Clear();
        Latest = null;
        LatestCamera = null;
        // Committed events remain drainable for logging; sequence and overflow count are lifetime totals.
    }

    private sealed record RawSample(bool Flag, string Offence, ulong Amount);
    private sealed record PendingSample(RawSample Raw, VehicleSnapshot Pose,
        DateTimeOffset ObservedAt, FineObservationSignal Signal);
}
