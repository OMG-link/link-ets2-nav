using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class OptionalSpeechModelTests
{
    public enum ModelProblem { Missing, EmptyTokens, SharingViolation, AccessDenied }

    [Theory]
    [InlineData(ModelProblem.Missing)]
    [InlineData(ModelProblem.EmptyTokens)]
    [InlineData(ModelProblem.SharingViolation)]
    [InlineData(ModelProblem.AccessDenied)]
    public async Task UnavailableModelUsesToneWhileRecordedSentencesAndCityFragmentsKeepPlaying(ModelProblem problem)
    {
        string host = Directory.CreateTempSubdirectory("nav-optional-speech-").FullName;
        Action? restore = null;
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
            string modelPath = Path.Combine(options.ModelDirectory, "model.onnx");
            string? accessError = null;
            if (problem == ModelProblem.EmptyTokens)
            {
                Directory.CreateDirectory(options.ModelDirectory);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "model.onnx"), [1]);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "voices.bin"), [1]);
                File.WriteAllBytes(Path.Combine(options.ModelDirectory, "tokens.txt"), []);
            }
            if (problem is ModelProblem.SharingViolation or ModelProblem.AccessDenied)
            {
                Directory.CreateDirectory(options.ModelDirectory);
                if (problem == ModelProblem.SharingViolation)
                {
                    File.WriteAllBytes(modelPath, [1]);
                    var locked = File.Open(modelPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    restore = locked.Dispose;
                    accessError = Assert.Throws<IOException>(ProbeRead).Message;
                }
                else
                {
                    if (OperatingSystem.IsWindows())
                    {
                        File.WriteAllBytes(modelPath, [1]);
                        var file = new FileInfo(modelPath);
                        var denied = file.GetAccessControl();
                        using var user = WindowsIdentity.GetCurrent();
                        var rule = new FileSystemAccessRule(user.User!, FileSystemRights.ReadData, AccessControlType.Deny);
                        denied.AddAccessRule(rule);
                        file.SetAccessControl(denied);
                        restore = () => { denied.RemoveAccessRuleSpecific(rule); file.SetAccessControl(denied); };
                    }
                    else
                    {
                        // Also works when tests run as root.
                        Directory.CreateDirectory(modelPath);
                        restore = () => Directory.Delete(modelPath);
                    }
                    accessError = Assert.Throws<UnauthorizedAccessException>(ProbeRead).Message;
                }
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
            string description = accessError is null ? "模型文件缺失或为空" : "模型文件无法读取";
            string warning = Assert.Single(messages, message => message.Contains(description, StringComparison.Ordinal));
            Assert.Contains(problem == ModelProblem.EmptyTokens ? "tokens.txt" : "model.onnx", warning);
            if (accessError is not null)
            {
                Assert.Contains(accessError, warning);
                restore!();
                restore = null;
                File.WriteAllBytes(modelPath, [1]);
                ProbeRead();
                // The same worker must reach the next input instead of caching its initial exception.
                playback.Notify("test", new("retry", "新的临时提醒。"));
                Assert.Equal("tone.wav", await player.Next(token));
                Assert.Contains(messages, m => m.Contains("模型文件缺失或为空") && m.Contains("voices.bin"));
            }

            // A new recording for a previously unavailable sentence must become playable after switching packs.
            string replacement = Path.Combine(host, "replacement");
            WritePack(replacement, (unknown, 5));
            await settings.SaveAsync(replacement, token);
            Assert.True(await resources.ApplyPendingAsync(queue));
            Assert.False(queue.IsUnavailable(unknown));
            playback.Notify("test", new("new-recording", unknown));
            Assert.Equal(new byte[] { 5, 0 }, PcmAudio.Read(await player.Next(token)));
            playback.ThrowIfFaulted();

            void ProbeRead() { using var file = File.OpenRead(modelPath); }
        }
        finally { try { restore?.Invoke(); } finally { Directory.Delete(host, true); } }
    }

    [Fact]
    public async Task CacheDirectoryFailureRemainsAWorkerError()
    {
        string host = Directory.CreateTempSubdirectory("nav-speech-cache-error-").FullName;
        try
        {
            string cache = Path.Combine(host, "cache");
            File.WriteAllBytes(cache, [1]);
            foreach (string input in new[] { "model.onnx", "voices.bin", "tokens.txt", "lexicon-us-en.txt", "lexicon-zh.txt" })
                File.WriteAllBytes(Path.Combine(host, input), [1]);
            await using var worker = new KokoroSpeechProcess(new SpeechOptions {
                Enabled = true, ModelDirectory = host, CacheDirectory = cache, PackDirectory = Path.Combine(host, "pack"),
                WorkerPath = typeof(SpeechPack).Assembly.Location
            });
            var failure = await Assert.ThrowsAsync<IOException>(() => worker.SynthesizeAsync("临时提醒。", TestContext.Current.CancellationToken));
            Assert.Contains(cache, failure.Message);
        }
        finally { Directory.Delete(host, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingPreferenceSurvivesPackSwitchAndAnUncoveredCueDoesNotStopPlayback(bool preparedOnly)
    {
        string host = Directory.CreateTempSubdirectory("nav-recording-mode-").FullName;
        try
        {
            const string first = "测试用已录音提示。", missing = "测试用尚未录音提示。";
            WritePack(Path.Combine(host, "pack"), (first, 7));
            var config = new Dictionary<string, object> {
                ["packDirectory"] = "pack", ["workerPath"] = typeof(SpeechPack).Assembly.Location, ["modelDirectory"] = "absent-model"
            };
            if (preparedOnly) config["preparedSpeechOnly"] = true; // Old settings omit this property.
            string settingsDirectory = Directory.CreateDirectory(Path.Combine(host, "Settings")).FullName;
            File.WriteAllText(Path.Combine(settingsDirectory, "LinkEts2Nav.Speech.json"), JsonSerializer.Serialize(config));
            var options = SpeechOptions.Load(host);
            Assert.True(options.Enabled);
            Assert.Equal(preparedOnly, options.PreparedSpeechOnly);
            var reason = preparedOnly ? SpeechUnavailableReason.MissingRecording : SpeechUnavailableReason.MissingModel;

            var token = TestContext.Current.CancellationToken;
            var settings = new SpeechPackSettings(host);
            var resources = new ReloadableSpeech(options, settings);
            var messages = new ConcurrentQueue<string>();
            await using var queue = new SpeechAnnouncementQueue(resources, _ => { }, messages.Enqueue, token, resources.Fixed);
            var player = new Player();
            await using var playback = new GuidancePlaybackManager(queue, player, () => "tone.wav", cancellationToken: token);
            var failure = await Assert.ThrowsAsync<SpeechUnavailableException>(() => resources.SynthesizeAsync(missing, token));
            Assert.Equal(reason, failure.Reason);
            Assert.Equal(new byte[] { 7, 0 }, PcmAudio.Read(await resources.SynthesizeAsync(first, token)));
            playback.Notify("test", new("missing", missing));
            Assert.Equal("tone.wav", await player.Next(token));
            Assert.Contains(messages, line => line.Contains(preparedOnly ? "仅使用预录语音" : "模型文件缺失或为空"));

            string replacement = Path.Combine(host, "replacement");
            WritePack(replacement, (missing, 8));
            await settings.SaveAsync(replacement, token);
            Assert.True(await resources.ApplyPendingAsync(queue));
            playback.Notify("test", new("recorded", missing));
            Assert.Equal(new byte[] { 8, 0 }, PcmAudio.Read(await player.Next(token)));
            var afterSwitch = await Assert.ThrowsAsync<SpeechUnavailableException>(() => resources.SynthesizeAsync(first, token));
            Assert.Equal(reason, afterSwitch.Reason);
            Assert.Equal(preparedOnly, SpeechOptions.Load(host).PreparedSpeechOnly);
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
