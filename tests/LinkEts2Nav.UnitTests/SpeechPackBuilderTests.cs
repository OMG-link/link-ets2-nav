using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechPackBuilderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "nav-builder-" + Guid.NewGuid().ToString("N"));
    private Task<byte[]> Audio(string text, CancellationToken token) => Task.FromResult<byte[]>([1,0,2,0]);
    private PackManifest Manifest() => JsonSerializer.Deserialize<PackManifest>(
        File.ReadAllText(Path.Combine(directory,"manifest.json")))!;

    private sealed class NoSynthesis : ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Pre-generated speech should play without inference.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoFallback : IWarningAnnouncer
    {
        public void Announce(CameraWarning warning) => throw new InvalidOperationException("Unexpected fallback.");
    }

    [Fact]
    public async Task MixedCachedAndNewClipsKeepCachedTailAndLateDiscoveryPlaysImmediately()
    {
        const string far="五百米处，有测速照相。", generic="前方有测速照相。", near="二百米处，有测速照相。";
        var token = TestContext.Current.CancellationToken;
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",[far,near],Audio,cancellationToken:token);
        var generated = new List<string>();
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",[far,generic,near],
            (text,ct)=> { generated.Add(text); return Audio(text,ct); },cancellationToken:token);
        Assert.Equal(generic,Assert.Single(generated));
        Assert.Equal(new[]{far,generic,near},Manifest().Entries.Select(e=>e.Text));
        var preloaded = PreGeneratedSpeech.Load(directory);
        Assert.Equal(3,preloaded.Count);
        var played = new List<string>();
        await using var speech = new PlannedSpeechAnnouncer(new NoSynthesis(),new NoFallback(),played.Add,
            cancellationToken:token,preGenerated:preloaded);
        speech.Announce(new("camera",425.1,WarningDistanceKind.Road){StageDistanceMeters=500});
        Assert.Equal(preloaded[generic],Assert.Single(played));
        // A completely cached rerun must retain the complete manifest without an API call.
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",[far,generic,near],
            (_,_)=>throw new InvalidOperationException("Unexpected generation."),cancellationToken:token);
        Assert.Equal(3,Manifest().Entries.Length);
    }

    [Fact]
    public async Task InterruptedMixedRunPreservesLaterCachedEntriesAndResumesWithoutRegeneratingThem()
    {
        var token = TestContext.Current.CancellationToken;
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",["a","z"],Audio,cancellationToken:token);
        await Assert.ThrowsAsync<IOException>(()=> SpeechPackBuilder.BuildAsync(directory,"model","voice",["a","b","c","z"],
            (text,ct)=>text=="c"?throw new IOException("interrupted"):Audio(text,ct),cancellationToken:token));
        Assert.Equal(new[]{"a","b","z"},Manifest().Entries.Select(e=>e.Text).Order(StringComparer.Ordinal));
        var generated = new List<string>();
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",["a","b","c","z"],
            (text,ct)=> { generated.Add(text);return Audio(text,ct); },cancellationToken:token);
        Assert.Equal("c",Assert.Single(generated));
        Assert.Equal(4,Manifest().Entries.Length);
    }

    [Fact]
    public async Task CorruptedCachedClipIsRegeneratedAndManifestUpdated()
    {
        var token = TestContext.Current.CancellationToken;
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",["a"],Audio,cancellationToken:token);
        File.WriteAllText(Path.Combine(directory,SpeechPack.FileName("a")),"broken");
        int calls=0;
        await SpeechPackBuilder.BuildAsync(directory,"model","voice",["a"],
            (text,ct)=> { calls++;return Audio(text,ct); },cancellationToken:token);
        Assert.Equal(1,calls);
        Assert.Single(PreGeneratedSpeech.Load(directory));
    }
    public void Dispose(){if(Directory.Exists(directory)) Directory.Delete(directory,true);}
}
