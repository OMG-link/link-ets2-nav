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
        bool cityNames=args[0]=="--generate-city-names";
        if ((args.Length != 5 && args.Length != 7) || cityNames && args.Length != 7
            || args[1] != "--keys" || args[3] != "--output" || args.Length == 7 && args[5] != "--texts")
            throw new ArgumentException("Usage: --generate-pack --keys FILE --output DIRECTORY; or --generate-city-names --keys FILE --output DIRECTORY --texts UTF8_FILE");
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
        IEnumerable<string> texts=SpeechPack.DefaultTexts();
        if (args.Length == 7)
        {
            using var reader = new StreamReader(args[6], Encoding.UTF8, true);
            texts = SpeechTextCatalog.Read(reader);
        }
        if (!cityNames && texts.Count() > 256) throw new InvalidDataException("Fixed speech packs support at most 256 entries.");
        if(cityNames)
        {
            string[] requested=texts.ToArray();
            if(requested.Any(t=>t.Length>128||t.Any(char.IsControl)))throw new InvalidDataException("Invalid city name.");
            string manifest=Path.Combine(output,"manifest.json");
            if(File.Exists(manifest)&&new FileInfo(manifest).Length>2097152)throw new InvalidDataException("City-name manifest too large.");
            var existing=File.Exists(manifest)?JsonSerializer.Deserialize<PackManifest>(await File.ReadAllTextAsync(manifest)):null;
            if(existing is not null&&(existing.Schema!=1||existing.Model!=Model||existing.Voice!=Voice||existing.Entries is null||existing.Entries.Length>4096||existing.Entries.Any(e=>e is null)))
                throw new InvalidDataException("Cannot extend an incompatible city-name pack.");
            texts=(existing?.Entries.Select(e=>e.Text)??[]).Concat(requested).Distinct(StringComparer.Ordinal).ToArray();
            if(texts.Count()>4096)throw new InvalidDataException("Too many city names.");
        }
        var pack = await SpeechPackBuilder.BuildAsync(output, Model, Voice, texts,
            async (text, token) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                return await GenerateAsync(key, text, timeout.Token,cityNames?"请用文本所属的原始语言自然准确朗读地名，不要翻译，只读地名，不加其他内容。":null);
            }, Console.WriteLine);
        Console.WriteLine("Speech pack ready: " + pack.Entries.Length + " entries.");
        return 0;
    }

    private static async Task<byte[]> GenerateAsync(string key, string text, CancellationToken token,string? instruction=null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "bearer " + key);
        await socket.ConnectAsync(new Uri(Endpoint), token);
        string id = Guid.NewGuid().ToString("N");
        object Header(string action) => new { action, task_id = id, streaming = "duplex" };
        async Task Send(object message) => await socket.SendAsync(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message,new JsonSerializerOptions{DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull})).AsMemory(), WebSocketMessageType.Text, true, token);
        await Send(new { header = Header("run-task"), payload = new {
            task_group = "audio", task = "tts", function = "SpeechSynthesizer", model = Model,
            parameters = new { text_type = "PlainText", voice = Voice, format = "pcm", sample_rate = 24000,
                volume = 50, rate = 1, pitch = 1, enable_ssml = false, instruction }, input = new { } } });
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
