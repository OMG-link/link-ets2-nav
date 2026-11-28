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
    // A recording can still play in full when its optional origin metadata cannot be
    // interpreted. Preserve this restriction if a later generation copies the recording.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExactOnly { get; init; }
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
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Schema", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Speech pack Schema must be 1.");
        if (!root.TryGetProperty("Entries", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Speech pack Entries must be an array.");
        int count = items.GetArrayLength();
        if (maximumEntries is int limit && count > limit) throw new InvalidDataException("Too many speech pack entries.");
        var entries = new PackEntry[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        bool packMetadataValid = true;
        string? model = OptionalString(root, "Model", ref packMetadataValid);
        string? voice = OptionalString(root, "Voice", ref packMetadataValid);
        string? profile = OptionalString(root, "ApiProfile", ref packMetadataValid);
        foreach (var item in items.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid speech pack entry.");
            string text = RequiredString(item, "Text");
            if (text.Length > 2048 || !seen.Add(text))
                throw new InvalidDataException("Invalid or duplicate speech pack text.");
            bool entryMetadataValid = packMetadataValid;
            string? voiceProfile = OptionalString(item, "VoiceProfile", ref entryMetadataValid);
            bool composed = OptionalFlag(item, "Composed", ref entryMetadataValid);
            bool exactOnly = OptionalFlag(item, "ExactOnly", ref entryMetadataValid);
            entries[i++] = new(text, RequiredString(item, "File"), RequiredString(item, "Sha256"))
            {
                Composed = composed, ExactOnly = exactOnly || !entryMetadataValid,
                VoiceProfile = entryMetadataValid ? voiceProfile : ""
            };
        }
        // Early fixed packs omitted model and voice. They can be played as whole recordings;
        // composition still requires an explicitly shared voice.
        return new(1, packMetadataValid ? model ?? "" : "", packMetadataValid ? voice ?? "" : "", entries)
            { ApiProfile = packMetadataValid ? profile : null };
    }

    private static string RequiredString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException($"Speech pack {name} must be a nonempty string.");

    private static string? OptionalString(JsonElement item, string name, ref bool valid)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        valid = false;
        return null;
    }

    private static bool OptionalFlag(JsonElement item, string name, ref bool valid)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.True) return true;
        valid = false;
        return false;
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
