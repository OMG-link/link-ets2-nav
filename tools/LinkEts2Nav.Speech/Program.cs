using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkEts2Nav.Speech;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Usage: LinkEts2Nav.Speech --model-dir DIR --cache-dir DIR [--voice zf_067] [--threads 2]");
    Console.WriteLine("Reads UTF-8 JSON lines {\"id\":\"request-id\",\"text\":\"完整短句\"} from stdin, in order.");
    Console.WriteLine("Writes {id,path,cacheHit,error} JSON lines to stdout. error=null indicates success.");
    Console.WriteLine("Kokoro multi-lang v1.1-zh preset: zf_067 = speaker 36, speed 1.0, CPU only.");
    Console.WriteLine("No playback or model downloads. EOF exits 0; startup errors exit 2; request errors keep the worker alive.");
    return 0;
}

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};
try
{
    var options = WorkerOptions.Parse(args);
    using var synthesis = new KokoroSynthesis(options);
    while (true)
    {
        string? id = null;
        try
        {
            var line = Protocol.ReadLine(Console.In);
            if (line is null) break;
            var request = JsonSerializer.Deserialize<Request>(line, json)
                ?? throw new InvalidDataException("Request must be a JSON object.");
            id = request.Id;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
                throw new InvalidDataException("Request id must contain 1-128 printable characters.");
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2048)
                throw new InvalidDataException("Text must contain 1-2048 characters.");
            var result = synthesis.Generate(request.Text.Trim().Normalize(NormalizationForm.FormC));
            Write(new Response(id, result.Path, result.CacheHit, null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Speech request failed: " + ex.Message);
            Write(new Response(id, null, false, ex.Message));
        }
    }
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine("Speech worker startup failed: " + ex.Message);
    return 2;
}

void Write(Response response)
{
    Console.WriteLine(JsonSerializer.Serialize(response, json));
    Console.Out.Flush();
}

internal sealed record Request(string Id, string Text);
internal sealed record Response(string? Id, string? Path, bool CacheHit, string? Error);
