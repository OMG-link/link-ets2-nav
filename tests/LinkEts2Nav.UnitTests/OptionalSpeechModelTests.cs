using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class OptionalSpeechModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingModelUsesToneWhileRecordedSentencesAndCityFragmentsKeepPlaying(bool emptyTokens)
    {
        string host = Directory.CreateTempSubdirectory("nav-optional-speech-").FullName;
        try
        {
            string pack = Path.Combine(host, "pack");
            const string fixedText = "导航开始。", unknown = "没有录音的临时提示。";
            WritePack(pack, (fixedText, 1), ("即将进入", 2), ("城区，请注意减速慢行。", 3));
            WritePack(Path.Combine(pack, "CityNames"), ("Oslo", 4));
            var options = new SpeechOptions {
                Enabled = true, PackDirectory = pack, ModelDirectory = Path.Combine(host, "model"),
                WorkerPath = typeof(SpeechPack).Assembly.Location, CacheDirectory = Path.Combine(host, "cache")
            };
            if (emptyTokens)
            {
                Directory.CreateDirectory(options.ModelDirectory);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "model.onnx"), [1]);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "voices.bin"), [1]);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "tokens.txt"), []);
            }

            var messages = new ConcurrentQueue<string>();
            var settings = new SpeechPackSettings(host);
            var resources = new ReloadableSpeech(options, settings, messages.Enqueue);
            var token = TestContext.Current.CancellationToken;
            await using var queue = new SpeechAnnouncementQueue(resources, _ => { }, messages.Enqueue, token, resources.Fixed);
            var player = new Player();
            await using var playback = new GuidancePlaybackManager(queue, player, () => "tone.wav", cancellationToken: token);

            playback.Notify("test", new("unknown", unknown));
            Assert.Equal("tone.wav", await player.Next(token));
            Assert.True(queue.IsUnavailable(unknown));
            playback.Notify("test", new("fixed", fixedText));
            Assert.Equal(resources.Fixed[fixedText], await player.Next(token));
            playback.Notify("test", new("city", "即将进入Oslo城区，请注意减速慢行。"));
            Assert.Equal(new byte[] { 2, 0, 4, 0, 3, 0 }, PcmAudio.Read(await player.Next(token)));
            playback.Notify("test", new("another", "另一句需要模型的提醒。"));
            Assert.Equal("tone.wav", await player.Next(token));
            string warning = Assert.Single(messages, message => message.Contains("模型文件缺失或为空", StringComparison.Ordinal));
            Assert.Contains(emptyTokens ? "tokens.txt" : "model.onnx", warning);

            // A new recording for a previously unavailable sentence must become playable after switching packs.
            string replacement = Path.Combine(host, "replacement");
            WritePack(replacement, (unknown, 5));
            await settings.SaveAsync(replacement, token);
            Assert.True(await resources.ApplyPendingAsync(queue));
            Assert.False(queue.IsUnavailable(unknown));
            playback.Notify("test", new("new-recording", unknown));
            Assert.Equal(new byte[] { 5, 0 }, PcmAudio.Read(await player.Next(token)));
            playback.ThrowIfFaulted();
        }
        finally { Directory.Delete(host, true); }
    }

    private static void WritePack(string directory, params (string Text, byte Sample)[] recordings)
    {
        Directory.CreateDirectory(directory);
        var entries = recordings.Select(recording =>
        {
            string file = SpeechPack.FileName(recording.Text);
            string path = Path.Combine(directory, file);
            PcmAudio.Write(path, [recording.Sample, 0]);
            return new PackEntry(recording.Text, file, SpeechPack.Hash(File.ReadAllBytes(path)));
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new PackManifest(1, "test", "test", entries)));
    }

    private sealed class Player : IGuidanceAudioPlayer
    {
        private readonly Channel<string> files = Channel.CreateUnbounded<string>();
        public Task PlayAsync(string file, CancellationToken token) => files.Writer.WriteAsync(file, token).AsTask();
        internal Task<string> Next(CancellationToken token) =>
            files.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
    }
}
