namespace LinkEts2Nav.SpeechProtocol;

// Shared by the host and its bundled worker. The grammar on stdout contains no user-supplied text.
internal sealed record GenerationJob(string Endpoint, string Model, string Voice, string? ApiKey, string RequestTemplate,
    string AuthHeader, string AuthPrefix, string[] Texts, string OutputDirectory, string CacheDirectory, string SourcePack)
{
    public const string Mode = "--pack-generation-worker";
    public override string ToString() => nameof(GenerationJob);
}
internal sealed record GenerationReply(string Kind, int Completed = 0, string? Code = null, int? HttpStatus = null);
