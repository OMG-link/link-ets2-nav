using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LinkEts2Nav.Map;

/// <summary>
/// Reads only PMG v0x15 locator metadata; does not decode meshes or allocate geometry.
/// Offsets are validated before access. Hookup offsets are relative to the string pool.
/// </summary>
public static class PmgLocatorReader
{
    private const int HeaderSize = 112;
    private const int LocatorSize = 44;
    private const int MaximumLocators = 100_000;
    private const int MaximumStringPool = 16 * 1024 * 1024;

    /// <summary>Reads metadata ranges from a seekable asset stream without buffering mesh geometry.</summary>
    public static IReadOnlyList<SpeedCameraLocator> Read(Stream stream,
        IReadOnlyDictionary<string, (float MeasurementDistance, bool ReverseDirection)> definitions)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("PMG metadata scanning requires a readable seekable stream.");
        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var header = new byte[HeaderSize];
            stream.ReadExactly(header);
            ValidateHeader(header);
            int count = ReadInt(header, 20);
            int partCount = ReadInt(header, 8);
            int partOffset = ReadInt(header, 76);
            int locatorOffset = ReadInt(header, 80);
            int stringOffset = ReadInt(header, 88);
            int stringSize = ReadInt(header, 92);
            ValidateSizes(count, stringSize);
            if (partCount < 0 || partCount > MaximumLocators) throw new InvalidDataException("Invalid PMG part count.");
            int partBytes = checked(partCount * 24);
            CheckRange(stream.Length, partOffset, partBytes);
            int locatorBytes = checked(count * LocatorSize);
            CheckRange(stream.Length, locatorOffset, locatorBytes);
            CheckRange(stream.Length, stringOffset, stringSize);
            var metadata = new byte[checked(HeaderSize + partBytes + locatorBytes + stringSize)];
            header.CopyTo(metadata, 0);
            stream.Position = partOffset;
            stream.ReadExactly(metadata.AsSpan(HeaderSize, partBytes));
            stream.Position = locatorOffset;
            stream.ReadExactly(metadata.AsSpan(HeaderSize + partBytes, locatorBytes));
            stream.Position = stringOffset;
            stream.ReadExactly(metadata.AsSpan(HeaderSize + partBytes + locatorBytes, stringSize));
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(76), HeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(80), HeaderSize + partBytes);
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(88), HeaderSize + partBytes + locatorBytes);
            return Read(metadata, definitions);
        }
        finally { stream.Position = originalPosition; }
    }

    public static IReadOnlyList<SpeedCameraLocator> Read(ReadOnlySpan<byte> bytes,
        IReadOnlyDictionary<string, (float MeasurementDistance, bool ReverseDirection)> definitions)
    {
        ValidateHeader(bytes);
        int count = ReadInt(bytes, 20);
        int locatorOffset = ReadInt(bytes, 80);
        int stringOffset = ReadInt(bytes, 88);
        int stringSize = ReadInt(bytes, 92);
        ValidateSizes(count, stringSize);
        CheckRange(bytes, locatorOffset, checked((long)count * LocatorSize));
        CheckRange(bytes, stringOffset, stringSize);
        int partCount = ReadInt(bytes, 8), partOffset = ReadInt(bytes, 76);
        if (partCount < 0 || partCount > MaximumLocators) throw new InvalidDataException("Invalid PMG part count.");
        CheckRange(bytes, partOffset, partCount * 24L);
        var partIndices = Enumerable.Repeat(-1, count).ToArray();
        for (int part = 0; part < partCount; part++)
        {
            int first = ReadInt(bytes, partOffset + part * 24 + 20);
            int length = ReadInt(bytes, partOffset + part * 24 + 16);
            if (first < 0 || length < 0 || first > count || length > count - first)
                throw new InvalidDataException("PMG part locator range is invalid.");
            for (int i = first; i < first + length; i++)
            {
                if (partIndices[i] != -1) throw new InvalidDataException("PMG locator belongs to multiple parts.");
                partIndices[i] = part;
            }
        }
        var result = new List<SpeedCameraLocator>();
        for (int i = 0; i < count; i++)
        {
            var item = bytes.Slice(locatorOffset + i * LocatorSize, LocatorSize);
            int hookupOffset = ReadInt(item, 40);
            if (hookupOffset < 0) continue;
            if (hookupOffset >= stringSize)
                throw new InvalidDataException("PMG locator hookup points outside the string pool.");
            var tail = bytes.Slice(stringOffset + hookupOffset, stringSize - hookupOffset);
            int terminator = tail.IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("Unterminated PMG hookup name.");
            string hookup = Encoding.UTF8.GetString(tail[..terminator]);
            if (!definitions.TryGetValue(hookup, out var definition)) continue;
            float scale = ReadFloat(item, 20);
            if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("PMG locator scale must be finite and positive.");
            var position = new Vector3(ReadFloat(item, 8), ReadFloat(item, 12), ReadFloat(item, 16));
            var rotation = new Quaternion(ReadFloat(item, 28), ReadFloat(item, 32), ReadFloat(item, 36), ReadFloat(item, 24));
            if (!Finite(position) || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 0.001f)
                throw new InvalidDataException("PMG locator has invalid placement.");
            // Stable locator index avoids depending on decoding SCS token names.
            result.Add(new SpeedCameraLocator(i.ToString(System.Globalization.CultureInfo.InvariantCulture), hookup,
                position, Quaternion.Normalize(rotation), definition.MeasurementDistance, definition.ReverseDirection) { PartIndex = partIndices[i], Scale = scale });
        }
        return result;
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static float ReadFloat(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(ReadInt(bytes, offset));
    private static void CheckRange(ReadOnlySpan<byte> bytes, int offset, long length) =>
        CheckRange(bytes.Length, offset, length);
    private static void CheckRange(long fileLength, int offset, long length)
    {
        if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
            throw new InvalidDataException("PMG section points outside the file.");
    }
    private static void ValidateHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || bytes[0] != 0x15 || bytes[1] != 'g' || bytes[2] != 'm' || bytes[3] != 'P')
            throw new InvalidDataException("Unsupported or truncated PMG header (expected version 0x15).");
    }
    private static void ValidateSizes(int count, int stringSize)
    {
        if (count < 0 || count > MaximumLocators || stringSize < 0 || stringSize > MaximumStringPool)
            throw new InvalidDataException("PMG locator metadata exceeds supported limits.");
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
