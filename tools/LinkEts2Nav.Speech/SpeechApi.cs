using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkEts2Nav.Speech;

/// <summary>A user-configured JSON POST returning one WAV. Credentials are read only for a real request.</summary>
internal sealed record SpeechApi(string Endpoint, string Model, string Voice, JsonElement Request, SpeechApi.AuthHeader? Auth)
{
    internal sealed record AuthHeader(string EnvironmentVariable, string Header = "Authorization", string Prefix = "Bearer ");

    public static async Task<SpeechApi> ReadAsync(string path)
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var api = JsonSerializer.Deserialize<SpeechApi>(await File.ReadAllTextAsync(path), json);
        if (api is null || !Uri.TryCreate(api.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0
            || api.Request.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected an HTTP(S) endpoint and a JSON request object.");
        if (api.Auth is { } auth)
        {
            using var probe = new HttpRequestMessage();
            if (string.IsNullOrWhiteSpace(auth.EnvironmentVariable) || auth.EnvironmentVariable.Any(c => c == '=' || char.IsControl(c))
                || string.IsNullOrWhiteSpace(auth.Header) || auth.Prefix is null || auth.Prefix.Any(char.IsControl)
                || !probe.Headers.TryAddWithoutValidation(auth.Header, "test"))
                throw new InvalidDataException("Invalid speech API authentication configuration.");
        }
        _ = api.Body(""); // Establish the template contract before acquiring audio or modifying an output.
        return api;
    }

    public string Profile => SpeechPack.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Endpoint, Model, Voice, Request, Auth }));

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
                    writer.WriteStringValue(Model);
                    break;
                case JsonValueKind.String when node.GetString() == "{{voice}}":
                    writer.WriteStringValue(Voice);
                    break;
                default:
                    node.WriteTo(writer);
                    break;
            }
        }
        Write(Request);
        if (!inserted) throw new InvalidDataException("The request needs a JSON string value equal to {{text}}.");
        writer.Flush();
        return result.ToArray();
    }

    public async Task<byte[]> GenerateAsync(HttpClient client, string text, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(Body(text)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = Encoding.UTF8.WebName };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        if (Auth is { } auth)
        {
            string? secret = Environment.GetEnvironmentVariable(auth.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl))
                throw new InvalidDataException("The configured speech API credential is missing or invalid.");
            message.Headers.TryAddWithoutValidation(auth.Header, auth.Prefix + secret);
        }
        using var response = await client.SendAsync(message, token);
        // An API may echo submitted credentials in its reason phrase or error body.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Speech API returned HTTP " + (int)response.StatusCode + ".");
        using var wav = await response.Content.ReadAsStreamAsync(token);
        return PcmAudio.Read(wav);
    }
}
