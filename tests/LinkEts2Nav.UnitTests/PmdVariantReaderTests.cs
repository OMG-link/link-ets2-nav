using System.Buffers.Binary;
using LinkEts2Nav.Map;
using TruckLib;

namespace LinkEts2Nav.UnitTests;

public sealed class PmdVariantReaderTests
{
    [Fact]
    public void DisabledHookupPartIsExcludedFromSelectedVariant()
    {
        var bytes = Pmd();
        Assert.Equal(2, PmdVariantReader.ReadVisibleParts(bytes, "default").Count);
        Assert.Equal([0], PmdVariantReader.ReadVisibleParts(bytes, "def_no_trig").Order().ToArray());
    }

    [Fact]
    public void UnknownVariantIsNeverAssumedDefault()
    {
        Assert.Throws<InvalidDataException>(() => PmdVariantReader.ReadVisibleParts(Pmd(), "unknown"));
    }

    [Fact]
    public void InvalidPartAttributeRangeIsRejected()
    {
        var bytes = Pmd();
        PutInt(bytes, 84, 5);
        Assert.Throws<InvalidDataException>(() => PmdVariantReader.ReadVisibleParts(bytes, "default"));
    }

    private static byte[] Pmd()
    {
        var bytes = new byte[144];
        PutInt(bytes, 0, 4); PutInt(bytes, 16, 2); PutInt(bytes, 20, 2); PutInt(bytes, 24, 2);
        PutInt(bytes, 40, 64); PutInt(bytes, 44, 80); PutInt(bytes, 48, 128); PutInt(bytes, 52, 96);
        PutToken(bytes, 64, "default"); PutToken(bytes, 72, "def_no_trig");
        PutInt(bytes, 80, 0); PutInt(bytes, 84, 1); PutInt(bytes, 88, 1); PutInt(bytes, 92, 2);
        PutToken(bytes, 96, "visible"); PutToken(bytes, 112, "visible");
        PutInt(bytes, 124, 4);
        PutInt(bytes, 128, 1); PutInt(bytes, 132, 1); PutInt(bytes, 136, 1); PutInt(bytes, 140, 0);
        return bytes;
    }
    private static void PutInt(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void PutToken(byte[] bytes, int offset, string value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), new Token(value).Value);
}
