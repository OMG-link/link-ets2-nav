using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinkEts2Nav.SpeechProtocol;

namespace LinkEts2Nav.Speech;

/// <summary>A user-configured JSON POST returning one WAV. The secret is received over the parent pipe only.</summary>
internal sealed class SpeechApi(GenerationJob job, JsonElement request)
{
    public static SpeechApi Create(GenerationJob job)
    {
        if (!Uri.TryCreate(job.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(job.Model) || string.IsNullOrWhiteSpace(job.Voice))
            throw new PackGenerationException("api");
        if (job.TokenCountEndpoint is { Length: > 0 } counter &&
            (!Uri.TryCreate(counter, UriKind.Absolute, out var countUri) || countUri.Scheme != endpoint.Scheme
            || !countUri.IdnHost.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) || countUri.Port != endpoint.Port
            || countUri.UserInfo.Length != 0 || countUri.Fragment.Length != 0))
            throw new PackGenerationException("token-count");
        using var doc = JsonDocument.Parse(job.RequestTemplate);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new PackGenerationException("template");
        using var probe = new HttpRequestMessage();
        if (job.ApiKey is { Length: > 0 } key && (key.Any(char.IsControl) || string.IsNullOrWhiteSpace(job.AuthHeader)
            || job.AuthPrefix is null || job.AuthPrefix.Any(char.IsControl) || !probe.Headers.TryAddWithoutValidation(job.AuthHeader, "test")))
            throw new PackGenerationException("auth");
        var api = new SpeechApi(job, doc.RootElement.Clone());
        _ = api.Body(""); // Establish the template contract before acquiring audio or modifying an output.
        return api;
    }

    public string Profile => SpeechPack.Hash(JsonSerializer.SerializeToUtf8Bytes(new {
        job.Endpoint, job.Model, job.Voice, Request = request, job.AuthHeader, job.AuthPrefix }));

    private byte[] Body(string text)
    {
        using var result = new MemoryStream();
        using var writer = new Utf8JsonWriter(result);
        bool inserted = false;
        void Write(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var part in node.EnumerateObject()) { writer.WritePropertyName(part.Name); Write(part.Value); }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var part in node.EnumerateArray()) Write(part);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String when node.GetString() == "{{text}}":
                    inserted = true;
                    writer.WriteStringValue(text);
                    break;
                case JsonValueKind.String when node.GetString() == "{{model}}":
                    writer.WriteStringValue(job.Model);
                    break;
                case JsonValueKind.String when node.GetString() == "{{voice}}":
                    writer.WriteStringValue(job.Voice);
                    break;
                default:
                    node.WriteTo(writer);
                    break;
            }
        }
        Write(request);
        if (!inserted) throw new PackGenerationException("template");
        writer.Flush();
        return result.ToArray();
    }

    private HttpRequestMessage Request(string endpoint, string text, string accept)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(Body(text)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (!string.IsNullOrEmpty(job.ApiKey)) message.Headers.TryAddWithoutValidation(job.AuthHeader, job.AuthPrefix + job.ApiKey);
        return message;
    }

    public async Task<long> CountAsync(HttpClient client, string text, CancellationToken token)
    {
        if (string.IsNullOrEmpty(job.TokenCountEndpoint)) return Encoding.UTF8.GetByteCount(text);
        using var message = Request(job.TokenCountEndpoint, text, "application/json");
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new PackGenerationException("token-count");
        try
        {
            await response.Content.LoadIntoBufferAsync(16384, token);
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tokens", out var count)
                && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out long value) && value >= 0) return value;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException) { }
        throw new PackGenerationException("token-count");
    }

    public async Task<byte[]> GenerateAsync(HttpClient client, string text, CancellationToken token)
    {
        using var message = Request(job.Endpoint, text, "audio/wav");
        using var response = await client.SendAsync(message, token);
        // An API may echo submitted credentials in its reason phrase or error body.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Speech API returned an error.", null, response.StatusCode);
        using var wav = await response.Content.ReadAsStreamAsync(token);
        try { return PcmAudio.Read(wav); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { throw new PackGenerationException("wav"); }
    }
}
