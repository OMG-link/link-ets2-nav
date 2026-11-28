using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Speech;

// Only verified recordings of a single declared voice can make up one utterance.
internal sealed class NavigationSpeechPack
{
    internal const string DirectoryName = "Navigation";
    private const string Punctuation = "，。；、！？";
    private sealed record Clip(string Word, string Directory, PackEntry Entry);
    private readonly Dictionary<char, List<Clip>> words = [];
    private readonly Dictionary<Clip, byte[]?> samples = [];

    private static bool SameVoice(PackManifest first, PackManifest second) =>
        !string.IsNullOrWhiteSpace(first.Model) && !string.IsNullOrWhiteSpace(first.Voice) &&
        first.Model == second.Model && first.Voice == second.Voice && first.ApiProfile == second.ApiProfile &&
        (ReferenceEquals(first, second) || !string.IsNullOrEmpty(first.ApiProfile) || first.Model != "external" || first.Voice != "default");

    internal static bool InVoice(PackEntry e, PackManifest pack) => !e.Composed &&
        (e.VoiceProfile is null || e.VoiceProfile.Length != 0 && e.VoiceProfile == pack.ApiProfile);

    internal static PackManifest? Read(string directory)
    {
        string manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            var pack = SpeechPackFormat.ReadManifest(manifest);
            if (string.IsNullOrWhiteSpace(pack.Model) || string.IsNullOrWhiteSpace(pack.Voice) ||
                pack.Entries.Any(e => e.Text.Any(char.IsControl))) return null;
            return pack;
        }
        catch (Exception e) when (SpeechPackFormat.IsReadFailure(e)) { return null; }
    }

    internal static NavigationSpeechPack? Open(string root, PackManifest? fixedPack, PackManifest? namesVoice = null)
    {
        string directory = Path.Combine(root, DirectoryName);
        var navigation = Read(directory);
        var voice = namesVoice ?? navigation;
        if (voice is null) return null;
        var book = new NavigationSpeechPack();
        if (navigation is not null && SameVoice(navigation, voice)) book.Add(directory, navigation);
        if (fixedPack is not null && SameVoice(fixedPack, voice)) book.Add(root, fixedPack);
        if (book.words.Count == 0) return null;
        string journeyDirectory = Path.Combine(root, JourneySpeechPack.DirectoryName);
        var journey = Read(journeyDirectory);
        if (journey is not null && SameVoice(journey, voice)) book.Add(journeyDirectory, journey, numbersOnly: true);
        return book;
    }

    private void Add(string directory, PackManifest manifest, bool numbersOnly = false)
    {
        foreach (var e in manifest.Entries)
        {
            if (e is null || string.IsNullOrEmpty(e.Text) || e.Text.Length > 2048 || e.Text.Any(char.IsControl) || !InVoice(e, manifest)) continue;
            if (numbersOnly && !e.Text.All(JourneySpeechText.NumberSounds.Contains)) continue;
            string word = Pronunciation(e.Text).Trim('，','。','；','、','！','？');
            if (word.Length == 0) continue;
            // A suffix following a distance and a complete one-digit number can stand
            // alone. Do not build a spoken word such as 左转 out of isolated characters.
            if (word.Length == 1 && word[0] != '后' && !("零一二两三四五六七八九十".Contains(word[0]))) continue;
            if (!words.TryGetValue(word[0], out var clips)) words.Add(word[0], clips = []);
            clips.Add(new(word, directory, e));
        }
    }

    // Signs, Latin letters, measure words, ordinals and direction remain significant.
    // Internal punctuation belongs to the recording. Punctuation between clips becomes a pause.
    private static string Pronunciation(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsAsciiDigit(c)) { result.Append(c); continue; }
            int start = i;
            while (i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) i++;
            var digits = text.AsSpan(start, i - start + 1);
            if ((digits.Length == 1 || digits[0] != '0') && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                result.Append(JourneySpeechText.SpokenNumber(number));
            else
                foreach (char digit in digits) result.Append(JourneySpeechText.Digits[digit - '0']);
            if (i + 2 < text.Length && text[i + 1] == '.' && char.IsAsciiDigit(text[i + 2]))
            {
                result.Append('点');
                i++;
                while (i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])) result.Append(JourneySpeechText.Digits[text[++i] - '0']);
            }
        }
        return result.ToString();
    }

    internal byte[]? Render(string text)
    {
        string spoken = Pronunciation(text);
        if (spoken.Length == 0) return null;
        // Choose the fewest clips that cover every spoken character. Ties favor a longer
        // initial phrase. Replan around a missing/corrupt recording before trying a live voice.
        while (true)
        {
            var next = new Clip?[spoken.Length];
            var count = new int[spoken.Length + 1];
            Array.Fill(count, int.MaxValue, 0, spoken.Length);
            for (int i = spoken.Length - 1; i >= 0; i--)
            {
                if (Punctuation.Contains(spoken[i])) { count[i] = count[i + 1]; continue; }
                // One recording must cover a numeric expression, including decimal points.
                // A route whose speed or distance is missing can use the live voice.
                if (i > 0 && JourneySpeechText.NumberSounds.Contains(spoken[i - 1]) && JourneySpeechText.NumberSounds.Contains(spoken[i])) continue;
                if (!words.TryGetValue(spoken[i], out var choices)) continue;
                foreach (var clip in choices)
                {
                    int end = i + clip.Word.Length;
                    if (clip.Word == "后" && (i == 0 || spoken[i - 1] is not ('米' or '里'))) continue;
                    if (end > spoken.Length || count[end] == int.MaxValue || count[end] + 1 > count[i] ||
                        !spoken.AsSpan(i).StartsWith(clip.Word, StringComparison.Ordinal) ||
                        samples.TryGetValue(clip, out var prior) && prior is null) continue;
                    if (count[end] + 1 == count[i] && next[i]!.Word.Length >= clip.Word.Length) continue;
                    count[i] = count[end] + 1;
                    next[i] = clip;
                }
            }
            if (count[0] is 0 or int.MaxValue) return null;
            using var pcm = new MemoryStream();
            int position = 0, pauseSamples = 0;
            while (position < spoken.Length)
            {
                if (Punctuation.Contains(spoken[position]))
                {
                    // At 24 kHz these are 100 ms for a list, 150 ms for a clause, 220 ms for
                    // a sentence. Existing silence on the recordings counts toward the interval.
                    int pause = spoken[position++] switch { '、' => 2400, '，' or '；' => 3600, _ => 5280 };
                    pauseSamples = Math.Max(pauseSamples, pause);
                    continue;
                }
                var clip = next[position]!;
                if (!samples.TryGetValue(clip, out var data))
                {
                    try { data = SpeechPack.ReadVerified(clip.Entry, clip.Directory); }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { data = null; }
                    samples.Add(clip, data);
                }
                if (data is null) break;
                if (pauseSamples > 0 && !CompletePause(pcm, data, pauseSamples)) return null;
                pauseSamples = 0;
                if (!Append(pcm, data)) return null;
                position += clip.Word.Length;
            }
            if (position == spoken.Length) return pcm.ToArray();
        }
    }

    private static bool CompletePause(MemoryStream output, byte[] incoming, int wanted)
    {
        if (output.Length == 0) return true;
        int tail = (int)output.Length - 2, head = 0;
        var preceding = output.GetBuffer();
        // PCM16 magnitude 128 is about -48 dBFS. Count the quiet samples at both boundaries.
        static bool Quiet(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) is >= -128 and <= 128;
        while (wanted > 0 && tail >= 0 && Quiet(preceding, tail)) { wanted--; tail -= 2; }
        while (wanted > 0 && head < incoming.Length && Quiet(incoming, head)) { wanted--; head += 2; }
        if (2 * wanted > PcmAudio.MaximumWaveBytes - 44 - output.Length) return false;
        output.SetLength(output.Length + 2 * wanted);
        output.Position = output.Length;
        return true;
    }

    internal static bool Append(MemoryStream output, byte[] samples)
    {
        if (samples.Length > PcmAudio.MaximumWaveBytes - 44 - output.Length) return false;
        output.Write(samples);
        return true;
    }

    internal static (string Path, bool CacheHit) Save(string cache, string prefix, byte[] samples)
    {
        string path = Path.Combine(cache, prefix + "-" + SpeechPack.Hash(samples) + ".wav");
        if (File.Exists(path))
        {
            try { if (PcmAudio.Read(path).AsSpan().SequenceEqual(samples)) return (path, true); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        PcmAudio.Write(path, samples);
        return (path, false);
    }
}
