using System.Text;
namespace LinkEts2Nav.Speech;

/// <summary>Canonical 24 kHz mono PCM16; joins samples rather than concatenating WAV containers.</summary>
public static class PcmAudio
{
    internal const int MaximumWaveBytes = SpeechPackFormat.MaximumWaveBytes;
    public static byte[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    internal static byte[] Read(Stream stream) => SpeechPackFormat.ReadWave(stream);

    public static void Write(string path, byte[] samples)
    {
        if (samples.Length is 0 or > MaximumWaveBytes - 44 || samples.Length % 2 != 0) throw new InvalidDataException("Invalid PCM samples.");
        WriteFile(path, file =>
        {
            using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
            writer.Write(0x46464952u); writer.Write(checked(36u + (uint)samples.Length));
            writer.Write(0x45564157u); writer.Write(0x20746D66u); writer.Write(16u);
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(24000u);
            writer.Write(48000u); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write((uint)samples.Length); writer.Write(samples);
        });
    }
    internal static void Copy(string path, Stream source) => WriteFile(path, source.CopyTo);

    private static void WriteFile(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary)) write(file);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
