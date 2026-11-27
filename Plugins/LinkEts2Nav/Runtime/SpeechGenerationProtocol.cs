namespace LinkEts2Nav.SpeechProtocol;

internal sealed record GenerationCandidate(string Text, long PlayedCount, double PreparationMilliseconds);

// Shared by the host and its bundled worker. The grammar on stdout contains no user-supplied text.
internal sealed record GenerationJob(string Endpoint, string Model, string Voice, string? ApiKey, string RequestTemplate,
    string AuthHeader, string AuthPrefix, GenerationCandidate[] Candidates, string OutputDirectory, string CacheDirectory,
    string SourcePack, long TokenBudget, string? TokenCountEndpoint = null, string? LocalModelDirectory = null,
    int LocalThreads = 2, string? LocalCacheDirectory = null)
{
    public const string Mode = "--pack-generation-worker";
    public override string ToString() => nameof(GenerationJob);
}
internal sealed record GenerationReply(string Kind, int Completed = 0, string? Code = null, int? HttpStatus = null,
    int Total = 0, long Budget = 0, long SpentTokens = 0, int WholeCount = 0, int ComposedCount = 0);
