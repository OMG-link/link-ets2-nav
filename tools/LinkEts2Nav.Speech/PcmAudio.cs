using System.Text;
namespace LinkEts2Nav.Speech;

/// <summary>Canonical 24 kHz mono PCM16; joins samples rather than concatenating WAV containers.</summary>
public static class PcmAudio
{
    public static byte[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 44 || stream.Length > 64 * 1024 * 1024 ||
            reader.ReadUInt32() != 0x46464952 || reader.ReadUInt32() != stream.Length - 8 ||
            reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid WAV container.");
        bool format = false;
        byte[]? samples = null;
        while (stream.Position + 8 <= stream.Length)
        {
            uint tag = reader.ReadUInt32(), size = reader.ReadUInt32();
            long end = stream.Position + size;
            if (end > stream.Length) throw new InvalidDataException("Truncated WAV.");
            if (tag == 0x20746D66)
            {
                if (size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 ||
                    reader.ReadUInt32() != 24000 || reader.ReadUInt32() != 48000 ||
                    reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("Expected mono PCM16 at 24000 Hz.");
                format = true;
            }
            if (tag == 0x61746164)
            {
                if (!format || size == 0 || size % 2 != 0 || samples is not null)
                    throw new InvalidDataException("Invalid PCM data.");
                samples = reader.ReadBytes(checked((int)size));
            }
            stream.Position = end + (size & 1);
        }
        if (stream.Position != stream.Length || samples is null) throw new InvalidDataException("Missing WAV data.");
        return samples;
    }

    public static void Write(string path, byte[] samples)
    {
        if (samples.Length == 0 || samples.Length % 2 != 0) throw new InvalidDataException("Invalid PCM samples.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new BinaryWriter(File.Create(temporary), Encoding.ASCII))
            {
                writer.Write(0x46464952u); writer.Write(checked(36u + (uint)samples.Length));
                writer.Write(0x45564157u); writer.Write(0x20746D66u); writer.Write(16u);
                writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(24000u);
                writer.Write(48000u); writer.Write((ushort)2); writer.Write((ushort)16);
                writer.Write(0x61746164u); writer.Write((uint)samples.Length); writer.Write(samples);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
