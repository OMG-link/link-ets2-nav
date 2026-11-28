using System.Text.Json;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

internal static class JourneySpeechPack
{
    internal const string DirectoryName = "Journey";

    internal static (string Path, bool CacheHit)? TryGenerate(string directory, string cache, JourneySpeechText journey)
    {
        string root = Path.Combine(directory, DirectoryName), manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var stream = File.OpenRead(manifest);
            var pack = JsonSerializer.Deserialize<PackManifest>(stream);
            if (pack is not { Schema: 1, Entries: not null } || string.IsNullOrWhiteSpace(pack.Model)
                || string.IsNullOrWhiteSpace(pack.Voice)) throw new InvalidDataException("Invalid journey voice.");
            var entries = new Dictionary<string, PackEntry>(StringComparer.Ordinal);
            foreach (var e in pack.Entries)
            {
                if (e is null || string.IsNullOrWhiteSpace(e.Text)) throw new InvalidDataException("Invalid journey clip.");
                // A plain single-voice manifest is the import format. Mixed root-pack entries with
                // an explicitly unknown/foreign origin cannot establish a common recorded voice.
                if (e.Composed || e.VoiceProfile is not null && (e.VoiceProfile.Length == 0 || e.VoiceProfile != pack.ApiProfile)) continue;
                if (!entries.TryAdd(e.Text, e)) throw new InvalidDataException("Duplicate journey clip.");
            }

            var samples = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            using var output = new MemoryStream();
            bool Append(string word)
            {
                if (!entries.TryGetValue(word, out var entry)) return false;
                if (!samples.TryGetValue(word, out byte[]? pcm))
                {
                    try { pcm = SpeechPack.ReadVerified(entry, root); }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { pcm = null; }
                    samples.Add(word, pcm);
                }
                if (pcm is null) return false;
                if (pcm.Length > PcmAudio.MaximumWaveBytes - 44 - output.Length) throw new InvalidDataException("Journey audio is too long.");
                output.Write(pcm);
                return true;
            }
            bool Number(string number)
            {
                for (int start = 0; start < number.Length;)
                {
                    // Use the longest available number recording at each position. Single-digit
                    // and place-value recordings cover every number from the shared formatter.
                    int length = number.Length - start;
                    while (length > 0 && !Append(number.Substring(start, length))) length--;
                    if (length == 0) return false;
                    start += length;
                }
                return true;
            }
            if (!Append(journey.Heading) || (!Append(journey.DistanceClause) &&
                !(Append(JourneySpeechText.DistancePrefix) && Number(journey.DistanceNumber) && Append(journey.DistanceUnit))) ||
                (!Append(journey.TimeClause) && !(Append(JourneySpeechText.TimePrefix) && Number(journey.Minutes)
                    && Append(JourneySpeechText.TimeSuffix)))) return null;

            byte[] joined = output.ToArray();
            string path = Path.Combine(cache, "journey-" + SpeechPack.Hash(joined) + ".wav");
            if (File.Exists(path))
            {
                try { if (PcmAudio.Read(path).AsSpan().SequenceEqual(joined)) return (path, true); }
                catch (Exception e) when (e is IOException or InvalidDataException) { }
            }
            PcmAudio.Write(path, joined);
            return (path, false);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Journey recording unavailable: " + e.GetType().Name);
            return null;
        }
    }
}
