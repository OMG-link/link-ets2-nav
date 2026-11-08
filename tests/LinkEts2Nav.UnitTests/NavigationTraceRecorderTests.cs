using System.Text.Json;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationTraceRecorderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nav-motion-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static GameDataSnapshot Data(double x = 1) => new(
        new(x, 2, 3, .25, 10, 100, true, false, 1, Now),
        RouteSnapshot.Parse([new(123, 100, 10), new(456, 0, 0)], Now),
        RouteAvailability.Available, "session", true);
    private static NearbyMapSnapshot Map() => new(new(), [], [], []);
    private static NavDiagnosticSnapshot Diagnostic() => new(Now, "running", "Available", 0, []);
    [Fact]
    public async Task CapturesExactInputsAndReusesUnchangedRouteNodes()
    {
        await using (var writer = new NavigationTraceRecorder(root))
        {
            Assert.True(writer.Record(Data(), Map(), Diagnostic(), "resource", "game"));
            Assert.True(writer.Record(Data(2), Map(), Diagnostic(), "resource", "game"));
        }
        var lines = File.ReadAllLines(Path.Combine(root, "motion.jsonl"));
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(123UL, first.RootElement.GetProperty("Route").GetProperty("Nodes")[0].GetProperty("Uid").GetUInt64());
        Assert.Equal(.25, first.RootElement.GetProperty("Vehicle").GetProperty("HeadingTurns").GetDouble());
        Assert.Equal(2, second.RootElement.GetProperty("Vehicle").GetProperty("X").GetDouble());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("Route").GetProperty("Nodes").ValueKind);
        Assert.Equal(2L, second.RootElement.GetProperty("Sequence").GetInt64());
    }
    [Fact]
    public async Task RotatedFilesAreSelfContainedAndRetentionIsBounded()
    {
        await using (var writer = new NavigationTraceRecorder(root, maximumBytes: 1))
            for (int i = 0; i < 3; i++) Assert.True(writer.Record(Data(i), Map(), Diagnostic(), "resource", "game"));
        Assert.Equal(2, Directory.GetFiles(root).Length);
        foreach (var file in Directory.GetFiles(root))
        {
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(file)));
            Assert.Equal(2, json.RootElement.GetProperty("Route").GetProperty("Nodes").GetArrayLength());
        }
    }
    [Fact]
    public async Task InvalidOutputLocationDisablesTracingWithoutThrowingIntoPlugin()
    {
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "file");
        File.WriteAllText(file, "occupied");
        var errors = new List<string>();
        await using (var writer = new NavigationTraceRecorder(file, errors.Add))
            writer.Record(Data(), Map(), Diagnostic(), "resource", "game");
        Assert.Single(errors);
    }
    [Fact]
    public async Task SnapshotFreezesBothRotationsAndFutureDrivingCannotOverwriteIt()
    {
        string saved=Path.Combine(root,"saved");
        Directory.CreateDirectory(saved);
        await using var writer=new NavigationTraceRecorder(root,maximumBytes:1);
        Assert.True(writer.Record(Data(1),Map(),Diagnostic(),"resource","game"));
        Assert.True(writer.Record(Data(2),Map(),Diagnostic(),"resource","game"));
        Assert.Equal(2,await writer.SaveSnapshotAsync(saved,TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken));
        Assert.True(writer.Record(Data(3),Map(),Diagnostic(),"resource","game"));
        string next=Path.Combine(root,"next");Directory.CreateDirectory(next);
        await writer.SaveSnapshotAsync(next,TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken);
        var positions=Directory.GetFiles(saved).Select(file=>
        {
            using var frame=JsonDocument.Parse(Assert.Single(File.ReadAllLines(file)));
            Assert.Equal(2,frame.RootElement.GetProperty("Route").GetProperty("Nodes").GetArrayLength());
            return frame.RootElement.GetProperty("Vehicle").GetProperty("X").GetDouble();
        }).Order().ToArray();
        Assert.Equal(new double[]{1,2},positions);
    }

    [Fact]
    public async Task FailedSnapshotDoesNotStopRecordingOrLaterCaptures()
    {
        await using var writer=new NavigationTraceRecorder(root);
        Assert.True(writer.Record(Data(),Map(),Diagnostic(),"resource","game"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(()=>writer.SaveSnapshotAsync(Path.Combine(root,"missing"),TestContext.Current.CancellationToken));
        Assert.True(writer.Record(Data(2),Map(),Diagnostic(),"resource","game"));
        string saved=Path.Combine(root,"saved");Directory.CreateDirectory(saved);
        Assert.Equal(1,await writer.SaveSnapshotAsync(saved,TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5),TestContext.Current.CancellationToken));
        Assert.Equal(2,File.ReadAllLines(Path.Combine(saved,"motion.jsonl")).Length);
    }

    [Fact]
    public async Task SaveIncludesUserCommentAndDiagnosticMetadata()
    {
        await using var writer=new NavigationTraceRecorder(root);
        writer.Record(Data(),Map(),Diagnostic(),"resource","game");
        string? saved=null;
        ReplayCapture.Attach(writer);
        try
        {
            saved=await ReplayCapture.SaveAsync("匝道播报过早\n第二行评论").WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);
            Assert.Equal("匝道播报过早\n第二行评论",File.ReadAllText(Path.Combine(saved,"comment.txt")));
            using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(saved,"incident.json")));
            Assert.Equal("匝道播报过早\n第二行评论",metadata.RootElement.GetProperty("Comment").GetString());
            Assert.True(File.Exists(Path.Combine(saved,"motion.jsonl")));
            Assert.False(saved.EndsWith(".partial",StringComparison.Ordinal));
        }
        finally
        {
            ReplayCapture.Detach(writer);
            if(saved is not null && Path.GetFullPath(saved).StartsWith(Path.Combine(AppContext.BaseDirectory,"Cache","LinkEts2Nav","Replays")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                Directory.Delete(saved,true);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
