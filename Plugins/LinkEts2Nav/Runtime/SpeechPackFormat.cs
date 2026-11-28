using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// The standalone tool retains its public import/export DTOs. The host only reads packs.
#if SPEECH_PACK_TOOL
namespace LinkEts2Nav.Speech;
public
#else
namespace LinkEts2Nav.Runtime.SpeechFiles;
internal
#endif
sealed record PackEntry(string Text, string File, string Sha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Composed { get; init; }
    // Absent inherits the manifest profile. Empty means an unknown recording origin.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceProfile { get; init; }
}

#if SPEECH_PACK_TOOL
public
#else
internal
#endif
sealed record PackManifest(int Schema, string Model, string Voice, PackEntry[] Entries)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiProfile { get; init; }
}

internal static class SpeechPackFormat
{
    internal const int MaximumWaveBytes = 64 * 1024 * 1024;
    internal const int MaximumNameEntries = 4096, MaximumNameManifestBytes = 2 * 1024 * 1024;
    internal static bool IsReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException;

    internal static string FileName(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + ".wav";
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static PackManifest ReadManifest(string file, int? maximumEntries = null, int? maximumBytes = null,
        CancellationToken token = default)
    {
        using var stream = File.OpenRead(file);
        if (maximumBytes is int bytes && stream.Length > bytes) throw new InvalidDataException("Speech pack manifest is too large.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("Schema").GetInt32() != 1) throw new InvalidDataException("Unsupported speech pack schema.");
        var items = root.GetProperty("Entries");
        int count = items.GetArrayLength();
        if (maximumEntries is int limit && count > limit) throw new InvalidDataException("Too many speech pack entries.");
        var entries = new PackEntry[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        static string? Optional(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) ? value.GetString() : null;
        foreach (var item in items.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid speech pack entry.");
            string? text = Optional(item, "Text");
            if (text is not { Length: > 0 and <= 2048 } || !seen.Add(text))
                throw new InvalidDataException("Invalid or duplicate speech pack text.");
            entries[i++] = new(text, Optional(item, "File") ?? "", Optional(item, "Sha256") ?? "")
            {
                Composed = item.TryGetProperty("Composed", out var composed) && composed.GetBoolean(),
                VoiceProfile = Optional(item, "VoiceProfile")
            };
        }
        // Early fixed packs omitted model and voice. They can be played as whole recordings;
        // composition still requires an explicitly shared voice.
        return new(1, Optional(root, "Model") ?? "", Optional(root, "Voice") ?? "", entries) { ApiProfile = Optional(root, "ApiProfile") };
    }

    internal static string EntryPath(string directory, PackEntry entry)
    {
        if (entry.File != FileName(entry.Text)) throw new InvalidDataException("Invalid speech pack filename.");
        return Path.Combine(directory, entry.File);
    }

    internal static void ValidateEntry(string directory, PackEntry entry)
    {
        using var file = File.OpenRead(EntryPath(directory, entry));
        Verify(file, entry.Sha256);
    }

    internal static byte[] ReadEntry(string directory, PackEntry entry)
    {
        using var file = File.OpenRead(EntryPath(directory, entry));
        return ReadSamples(file, Verify(file, entry.Sha256));
    }

    internal static void VerifyWave(string path, string expectedHash)
    {
        using var file = File.OpenRead(path);
        Verify(file, expectedHash);
    }

    internal static (long Offset, int Length) Verify(Stream file, string expectedHash)
    {
        var data = FindSamples(file);
        file.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Speech pack checksum mismatch.");
        return data;
    }

    internal static void ValidateWave(string path)
    {
        using var file = File.OpenRead(path);
        FindSamples(file);
    }

    internal static byte[] ReadWave(Stream file) => ReadSamples(file, FindSamples(file));

    private static byte[] ReadSamples(Stream file, (long Offset, int Length) data)
    {
        file.Position = data.Offset;
        byte[] samples = GC.AllocateUninitializedArray<byte>(data.Length);
        file.ReadExactly(samples);
        return samples;
    }

    private static (long Offset, int Length) FindSamples(Stream file)
    {
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        long length = file.Length;
        if (length is < 44 or > MaximumWaveBytes || reader.ReadUInt32() != 0x46464952 ||
            reader.ReadUInt32() != length - 8 || reader.ReadUInt32() != 0x45564157)
            throw new InvalidDataException("Invalid WAV container.");
        bool format = false;
        (long Offset, int Length) data = default;
        while (file.Position + 8 <= length)
        {
            uint tag = reader.ReadUInt32(), size = reader.ReadUInt32();
            long next = file.Position + size + (size & 1);
            if (next > length) throw new InvalidDataException("Truncated WAV chunk.");
            if (tag == 0x20746D66)
            {
                if (size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 24000 ||
                    reader.ReadUInt32() != 48000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Expected mono PCM16 at 24000 Hz.");
                format = true;
            }
            if (tag == 0x61746164)
            {
                if (!format || size == 0 || (size & 1) != 0 || data.Length != 0) throw new InvalidDataException("Invalid PCM data.");
                data = (file.Position, checked((int)size));
            }
            file.Position = next; // Skip optional RIFF chunks and their word padding.
        }
        if (file.Position != length || data.Length == 0) throw new InvalidDataException("Missing or incomplete WAV data.");
        return data;
    }
}
