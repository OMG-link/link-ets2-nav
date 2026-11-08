using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
using LinkEts2Nav.Map;
using System.Numerics;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class LateWarningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nav-late-" + Guid.NewGuid().ToString("N"));
    private sealed class Synth : ISpeechSynthesizer
    {
        public int Calls;
        public Task<string> SynthesizeAsync(string text, CancellationToken token)
        { Calls++; throw new Exception("Pre-generated warning must not require inference."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fallback : IWarningAnnouncer
    {
        public int Calls;
        public void Announce(CameraWarning warning) => Calls++;
    }

    [Fact]
    public async Task FirstConfirmedTargetAt150MetersPlays200WithinImmediatelyWithoutPlanningOrFallback()
    {
        string text = "两百米内，有测速照相。", name = SpeechPack.FileName(text);
        string path = Path.Combine(root, name);
        PcmAudio.Write(path, [1, 0, 2, 0]);
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(
            new PackManifest(1, "model", "voice", [new(text, name, SpeechPack.Hash(File.ReadAllBytes(path)))])));
        var preloaded = PreGeneratedSpeech.Load(root);
        var synth = new Synth();
        var fallback = new Fallback();
        var played = new List<string>();
        await using var announcer = new PlannedSpeechAnnouncer(synth, fallback, played.Add, preGenerated: preloaded);
        var engine = new CameraWarningEngine();
        var now = DateTimeOffset.UtcNow;
        WarningSnapshot Snapshot(CameraApproach approach) => new("session", "", false, true, now,
            [new CameraCandidate("camera", approach, 145, 150)]);
        Assert.Empty(engine.Update(Snapshot(CameraApproach.Unknown), now));
        var warning = Assert.Single(engine.Update(Snapshot(CameraApproach.UnavoidableAhead), now));
        Assert.Equal(200, warning.StageDistanceMeters);
        announcer.Announce(warning);
        Assert.Equal(path, Assert.Single(played));
        Assert.Equal(0, synth.Calls);
        Assert.Equal(0, fallback.Calls);
        Assert.Empty(engine.Update(Snapshot(CameraApproach.UnavoidableAhead), now));
    }

    [Fact]
    public void CorruptPreloadedAudioIsNotPlayed()
    {
        Directory.CreateDirectory(root);
        string text = "两百米内，有测速照相。", name = SpeechPack.FileName(text);
        File.WriteAllText(Path.Combine(root, name), new string('x', 44));
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(
            new PackManifest(1, "model", "voice", [new(text, name, "invalid")])));
        Assert.Empty(PreGeneratedSpeech.Load(root));
    }

    [Fact]
    public void RoadEndpointProximityDoesNotStealPositionStillInsideJunction()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map, new(0,0,0), new(0,0,200), default);
        var outgoing = Road.Add(map, new(0,0,240), new(0,0,800), default);
        DirectedRoad junction = new("prefab:test:0:1", incoming.ForwardNode.Uid, outgoing.Node.Uid,
            [new(0,0,200), new(0,0,240)]);
        var source = new RoadPathSource(map, [junction]);
        var none = RouteSnapshot.Parse([], DateTimeOffset.UtcNow);
        var camera = new MonitoredCamera("camera", new(0,0,700), Vector3.UnitZ);
        foreach (float z in new[] { 229f, 234f, 238f, 239f, 240f, 241f, 246f })
        {
            var match = source.MatchCamera(new(3.5f,0,z), Vector3.UnitZ, camera, none, RouteAvailability.None);
            Assert.Equal(CameraApproach.UnavoidableAhead, match.Approach);
            Assert.Equal(700-z, match.RoadDistanceMeters!.Value, 2);
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
