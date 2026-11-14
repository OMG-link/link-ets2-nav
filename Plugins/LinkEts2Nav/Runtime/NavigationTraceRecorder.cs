using System.Text.Json;
using System.Threading.Channels;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Runtime;

/// <summary>Bounded local motion samples; file IO never runs on the warning loop.</summary>
public sealed class NavigationTraceRecorder : IAsyncDisposable
{
    private readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(64)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private long sequence;
    private readonly record struct Frame(long Sequence, GameDataSnapshot Data, NearbyMapSnapshot Map,
        NavDiagnosticSnapshot Diagnostic, string ResourceId, string GamePath);
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public NavigationTraceRecorder(string directory, Action<string>? onError = null, long maximumBytes = 8 * 1024 * 1024)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        worker = Task.Run(async () =>
        {
            try { await WriteAsync(directory, maximumBytes).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                queue.Writer.TryComplete();
                onError?.Invoke("运动采样已停止：" + ex.GetType().Name);
            }
        });
    }

    public bool Record(GameDataSnapshot data, NearbyMapSnapshot map, NavDiagnosticSnapshot diagnostic,
        string resourceId, string gamePath)
        => queue.Writer.TryWrite(new Frame(Interlocked.Increment(ref sequence), data, map, diagnostic, resourceId, gamePath));

    private sealed record SnapshotRequest(string Directory, TaskCompletionSource<int> Completion);

    public async Task<int> SaveSnapshotAsync(string directory, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new SnapshotRequest(directory, completion), cancellationToken);
        // If recording has failed or stopped, never leave a queued save waiting forever.
        var finished = await Task.WhenAny(completion.Task, worker);
        if (finished == worker && !completion.Task.IsCompleted)
            throw new IOException("运动记录已停止，无法固定保存回放。");
        return await completion.Task;
    }

    private async Task WriteAsync(string directory, long maximumBytes)
    {
        Directory.CreateDirectory(directory);
        string current = Path.Combine(directory, "motion.jsonl");
        string previous = Path.Combine(directory, "motion.previous.jsonl");
        if (File.Exists(current)) File.Move(current, previous, true);
        StreamWriter? output = null;
        long bytes = 0;
        string? lastNodes = null;
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (work is SnapshotRequest request)
                {
                    try
                    {
                        int count = 0;
                        foreach (string source in new[] { previous, current })
                            if (File.Exists(source))
                            {
                                await ReplayCapture.CopySharedAsync(source, Path.Combine(request.Directory, Path.GetFileName(source)));
                                count++;
                            }
                        request.Completion.TrySetResult(count);
                    }
                    catch (Exception ex) { request.Completion.TrySetException(ex); }
                    continue;
                }
                var frame = (Frame)work;
                var data = frame.Data;
                string nodes = JsonSerializer.Serialize(data.Route.Nodes, Json);
                string Serialize(bool includeNodes) => JsonSerializer.Serialize(new
                {
                    Schema = 1, frame.Sequence, frame.Diagnostic.UpdatedAt, frame.ResourceId, frame.GamePath,
                    data.SessionId, data.Vehicle, data.IsVehicleUsable, data.RouteAvailability,data.PositionSignal,data.FerryUse,data.FerryArrival,data.IsTransportPending,
                    Route = new { data.Route.Revision, data.Route.Availability, data.Route.ReceivedAt,
                        data.Route.MayBeTruncated, Nodes = includeNodes ? data.Route.Nodes : null },
                    frame.Map.Sectors, frame.Map.Cameras, Decisions = frame.Diagnostic.Cameras, frame.Diagnostic.Navigation, frame.Diagnostic.CurrentSpeedLimit, frame.Diagnostic.Guidance
                }, Json);
                string line = Serialize(nodes != lastNodes);
                int size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                if (output is not null && bytes + size > maximumBytes)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    output = null;
                    File.Move(current, previous, true);
                    bytes = 0;
                    // Every rotated file starts with its own full route, even if unchanged.
                    line = Serialize(true);
                    size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                }
                output ??= new StreamWriter(new FileStream(current, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
                await output.WriteLineAsync(line).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                bytes += size;
                lastNodes = nodes;
            }
        }
        finally { if (output is not null) await output.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }
}
