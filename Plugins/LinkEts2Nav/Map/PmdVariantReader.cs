using System.Buffers.Binary;
using TruckLib;

namespace LinkEts2Nav.Map;

/// <summary>Resolves active PMD parts using each part's attribute range and selected variant.</summary>
public static class PmdVariantReader
{
    private const int MaximumEntries = 100_000;
    private static readonly ulong VisibleTag = new Token("visible").Value;

    public static IReadOnlySet<int> ReadVisibleParts(ReadOnlySpan<byte> bytes, string variant)
    {
        if (bytes.Length < 64 || ReadInt(bytes, 0) != 4)
            throw new InvalidDataException("Unsupported or truncated PMD header (expected version 4).");
        int variants = ReadInt(bytes, 16), parts = ReadInt(bytes, 20), attributes = ReadInt(bytes, 24);
        if (variants <= 0 || variants > MaximumEntries || parts < 0 || parts > MaximumEntries || attributes < 0 || attributes > MaximumEntries)
            throw new InvalidDataException("PMD variant metadata exceeds supported limits.");
        int variantOffset = ReadInt(bytes, 40), partOffset = ReadInt(bytes, 44);
        int valueOffset = ReadInt(bytes, 48), attributeOffset = ReadInt(bytes, 52);
        Check(bytes.Length, variantOffset, variants * 8L);
        Check(bytes.Length, partOffset, parts * 8L);
        Check(bytes.Length, attributeOffset, attributes * 16L);
        Check(bytes.Length, valueOffset, variants * (long)attributes * 4);
        ulong requested = new Token(string.IsNullOrEmpty(variant) ? "default" : variant).Value;
        int selected = -1;
        for (int i = 0; i < variants; i++)
            if (ReadUlong(bytes, variantOffset + i * 8) == requested) { selected = i; break; }
        if (selected < 0) throw new InvalidDataException($"PMD variant '{variant}' does not exist.");
        var visible = new HashSet<int>();
        for (int part = 0; part < parts; part++)
        {
            int start = ReadInt(bytes, partOffset + part * 8), end = ReadInt(bytes, partOffset + part * 8 + 4);
            if (start < 0 || end < start || end > attributes)
                throw new InvalidDataException("PMD part attribute range is invalid.");
            bool? enabled = null;
            for (int attribute = start; attribute < end; attribute++)
            {
                int header = attributeOffset + attribute * 16;
                if (ReadUlong(bytes, header) != VisibleTag) continue;
                if (ReadInt(bytes, header + 8) != 0)
                    throw new InvalidDataException("Unsupported PMD visibility attribute type.");
                // Attribute values are stored sequentially per variant in PMD v4.
                int value = ReadInt(bytes, checked(valueOffset + (selected * attributes + attribute) * 4));
                if (value is not 0 and not 1) throw new InvalidDataException("Invalid PMD visibility flag.");
                enabled = value == 1;
            }
            // Do not infer visibility for a part without a supported explicit flag.
            if (enabled == true) visible.Add(part);
        }
        return visible;
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ulong ReadUlong(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Check(long length, int offset, long count)
    {
        if (offset < 0 || count < 0 || offset > length || count > length - offset)
            throw new InvalidDataException("PMD section points outside the file.");
    }
}
