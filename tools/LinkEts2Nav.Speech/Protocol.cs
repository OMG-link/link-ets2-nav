using System.Text;

namespace LinkEts2Nav.Speech;

internal static class Protocol
{
    // ReadLine() allocates without a bound. Oversize input is drained, never retained.
    public static string? ReadLine(TextReader input)
    {
        const int maximum = 16384;
        var line = new StringBuilder();
        bool tooLong = false;
        while (true)
        {
            int character = input.Read();
            if (character == -1 || character == '\n')
            {
                if (tooLong) throw new InvalidDataException("JSON line exceeds 16 KiB of characters.");
                if (character == -1 && line.Length == 0) return null;
                return line.ToString().TrimEnd('\r');
            }
            if (line.Length < maximum) line.Append((char)character);
            else tooLong = true;
        }
    }
}

internal sealed record WorkerOptions(string ModelDirectory, string CacheDirectory, string Voice, int Threads)
{
    public static WorkerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--model-dir" or "--cache-dir" or "--voice" or "--threads")
                || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Unknown, repeated or incomplete speech worker option; use --help.");
        }
        if (!values.TryGetValue("--model-dir", out string? model) || string.IsNullOrWhiteSpace(model)
            || !values.TryGetValue("--cache-dir", out string? cache) || string.IsNullOrWhiteSpace(cache))
            throw new ArgumentException("Both --model-dir and --cache-dir are required.");
        string voice = values.GetValueOrDefault("--voice", "zf_067");
        if (voice != "zf_067") throw new ArgumentException("This Kokoro v1.1-zh preset supports zf_067 only.");
        if (!int.TryParse(values.GetValueOrDefault("--threads", "2"), out int threads) || threads is < 1 or > 8)
            throw new ArgumentException("Threads must be between 1 and 8.");
        return new(Path.GetFullPath(model), Path.GetFullPath(cache), voice, threads);
    }
}
