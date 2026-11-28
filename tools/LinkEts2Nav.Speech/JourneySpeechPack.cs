using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

internal static class JourneySpeechPack
{
    internal const string DirectoryName = "Journey";

    internal sealed record Recording(byte[] Heading, byte[]? Distance, byte[]? Time)
    {
        internal (string Path, bool CacheHit)? Save(string cache, bool hybrid = false)
        {
            if (Distance is null || Time is null) return null;
            using var output = new MemoryStream();
            if (!NavigationSpeechPack.Append(output, Heading) || !NavigationSpeechPack.Append(output, Distance) ||
                !NavigationSpeechPack.Append(output, Time)) return null;
            return NavigationSpeechPack.Save(cache, hybrid ? "journey-hybrid" : "journey", output.ToArray());
        }
    }

    internal static Recording? Read(string directory, JourneySpeechText journey)
    {
        string root = Path.Combine(directory, DirectoryName);
        var pack = NavigationSpeechPack.Read(root);
        if (pack is null) return null;
        var entries = pack.Entries.Where(e => NavigationSpeechPack.InVoice(e, pack)).ToDictionary(e => e.Text, StringComparer.Ordinal);
        var samples = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Clip(string word)
        {
            if (!entries.TryGetValue(word, out var entry)) return null;
            if (!samples.TryGetValue(word, out var pcm))
            {
                try { pcm = SpeechPack.ReadVerified(entry, root); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { pcm = null; }
                samples.Add(word, pcm);
            }
            return pcm;
        }
        byte[]? Phrase(string text) => Clip(text) ?? Clip(text.TrimEnd('，', '。'));
        static byte[]? Join(params byte[][] clips)
        {
            using var output = new MemoryStream();
            foreach (var clip in clips)
                if (!NavigationSpeechPack.Append(output, clip)) return null;
            return output.ToArray();
        }
        byte[]? Quantity(string prefix, string number, string unit)
        {
            if (Phrase(prefix + number + unit) is { } clause) return clause;
            if (Clip(prefix) is not { } lead) return null;
            if (Phrase(number + unit) is { } quantity) return Join(lead, quantity);
            if (number.Length > 1 && Clip(number) is { } wholeNumber && Phrase(unit) is { } measure)
                return Join(lead, wholeNumber, measure);

            // Only split on a complete Chinese place-value group, e.g. 一百 + 二十二分钟.
            // A decimal point, an isolated digit or a succession of short number clips
            // cannot stand in for a naturally recorded quantity.
            for (int split = 2; split < number.Length; split++)
                if ("百千万亿".Contains(number[split - 1]) && !number[..split].Contains('点') &&
                    number[split] is not ('零' or '点') && Clip(number[..split]) is { } group &&
                    Phrase(number[split..] + unit) is { } ending)
                    return Join(lead, group, ending);
            return null;
        }
        return Phrase(journey.Heading) is { } heading ? new(heading,
            Quantity(JourneySpeechText.DistancePrefix, journey.DistanceNumber, journey.DistanceUnit),
            Quantity(JourneySpeechText.TimePrefix, journey.Minutes, JourneySpeechText.TimeSuffix)) : null;
    }
}
