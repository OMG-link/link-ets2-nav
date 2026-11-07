using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LinkEts2Nav.Speech;

internal static class QwenPackGenerator
{
    private const string Endpoint = "wss://cn-beijing.maas.qianwenaiapi.com/api-ws/v1/inference";
    private const string Model = "qwen-audio-3.1-tts-flash";
    private const string Voice = "longanhuan_v3.1";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || args[1] != "--keys" || args[3] != "--output")
            throw new ArgumentException("Usage: --generate-pack --keys FILE --output DIRECTORY");
        string? key = null;
        foreach (string line in await File.ReadAllLinesAsync(args[2]))
        {
            int split = line.IndexOf('=');
            if (split < 0 || line[..split].Trim() != "qwen") continue;
            if (key is not null) throw new InvalidDataException("Duplicate qwen key.");
            key = line[(split + 1)..].Trim();
        }
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new InvalidDataException("Missing or invalid qwen key.");
        string output = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(output);
        string manifestPath = Path.Combine(output, "manifest.json");
        PackManifest? previous = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(manifestPath)) : null;
        var entries = new List<PackEntry>();
        foreach (string text in SpeechPack.DefaultTexts())
        {
            string file = SpeechPack.FileName(text), path = Path.Combine(output, file);
            var cached = previous is { Schema: 1, Model: Model, Voice: Voice }
                ? previous.Entries.SingleOrDefault(e => e.Text == text && e.File == file) : null;
            if (cached is not null && File.Exists(path) && SpeechPack.Hash(await File.ReadAllBytesAsync(path)) == cached.Sha256)
            { PcmAudio.Read(path); entries.Add(cached); Console.WriteLine("Cached: " + text); continue; }
            Console.WriteLine("Generating: " + text);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            byte[] pcm = await GenerateAsync(key, text, timeout.Token);
            PcmAudio.Write(path, pcm);
            entries.Add(new(text, file, SpeechPack.Hash(await File.ReadAllBytesAsync(path))));
            // Checkpoint each success; interrupted runs resume without rebilling completed entries.
            string temporary = manifestPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new PackManifest(1, Model, Voice, entries.ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, manifestPath, true);
        }
        Console.WriteLine("Speech pack ready: " + entries.Count + " entries.");
        return 0;
    }

    private static async Task<byte[]> GenerateAsync(string key, string text, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "bearer " + key);
        await socket.ConnectAsync(new Uri(Endpoint), token);
        string id = Guid.NewGuid().ToString("N");
        object Header(string action) => new { action, task_id = id, streaming = "duplex" };
        async Task Send(object message) => await socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)).AsMemory(), WebSocketMessageType.Text, true, token);
        await Send(new { header = Header("run-task"), payload = new {
            task_group = "audio", task = "tts", function = "SpeechSynthesizer", model = Model,
            parameters = new { text_type = "PlainText", voice = Voice, format = "pcm", sample_rate = 24000,
                volume = 50, rate = 1, pitch = 1, enable_ssml = false }, input = new { } } });
        using var audio = new MemoryStream();
        byte[] buffer = new byte[16384];
        bool started = false;
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Qwen closed before task completion.");
                message.Write(buffer, 0, received.Count);
                if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("Qwen message too large.");
            } while (!received.EndOfMessage);
            if (received.MessageType == WebSocketMessageType.Binary)
            {
                if (!started) throw new InvalidDataException("Qwen audio arrived before task-started.");
                message.Position = 0; message.CopyTo(audio);
                if (audio.Length > 16 * 1024 * 1024) throw new InvalidDataException("Qwen audio too large.");
                continue;
            }
            using var json = JsonDocument.Parse(message.ToArray());
            var header = json.RootElement.GetProperty("header");
            if (header.GetProperty("task_id").GetString() != id) throw new InvalidDataException("Qwen task ID mismatch.");
            string? kind = header.GetProperty("event").GetString();
            if (kind == "task-failed")
            {
                // Never echo provider messages: a service error could contain request credentials.
                string code = header.TryGetProperty("error_code", out var error) ? error.GetString() ?? "" : "";
                throw new IOException("Qwen synthesis rejected (code " + new string(code.Where(char.IsLetterOrDigit).Take(80).ToArray()) + ").");
            }
            if (kind == "task-started" && !started)
            {
                started = true;
                await Send(new { header = Header("continue-task"), payload = new { input = new { text } } });
                await Send(new { header = Header("finish-task"), payload = new { input = new { } } });
            }
            if (kind == "task-finished")
            {
                if (!started || audio.Length == 0 || audio.Length % 2 != 0) throw new InvalidDataException("Qwen returned empty or invalid PCM.");
                return audio.ToArray();
            }
        }
    }
}
