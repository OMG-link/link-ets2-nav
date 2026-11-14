using System.Text;

namespace LinkEts2Nav.Speech;

/// <summary>The checked-in UTF-8 list is the sole source of default pack texts.</summary>
public static class SpeechTextCatalog
{
    public static IReadOnlyList<string> DefaultTexts { get; } = LoadDefault();

    public static string[] Read(TextReader reader)
    {
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string text = line.Trim().Normalize(NormalizationForm.FormC);
            if (text.Length == 0) continue;
            if (text.Length > 2048 || text.Any(char.IsControl))
                throw new InvalidDataException("Each speech text must contain 1..2048 printable characters.");
            if (!seen.Add(text)) throw new InvalidDataException("Duplicate speech text: " + text);
            texts.Add(text);
            if (texts.Count > 4096) throw new InvalidDataException("Too many speech texts.");
        }
        if (texts.Count == 0) throw new InvalidDataException("Speech text list is empty.");
        return texts.ToArray();
    }

    private static IReadOnlyList<string> LoadDefault()
    {
        using var stream = typeof(SpeechTextCatalog).Assembly.GetManifestResourceStream("LinkEts2Nav.Speech.zh-CN.txt")
            ?? throw new InvalidDataException("Default speech text list is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return Array.AsReadOnly(Read(reader));
    }
}
