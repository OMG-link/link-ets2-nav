using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class PmgLocatorReaderTests
{
    private static readonly Dictionary<string, (float MeasurementDistance, bool ReverseDirection)> Definitions = new()
    {
        ["speedcam.20"] = (20, false),
        ["speedcam.r"] = (20, true)
    };

    [Fact]
    public void ReadsHookupFromStringPoolInsteadOfTreatingOffsetAsAbsolute()
    {
        var bytes = Model("speedcam.r");
        var locator = Assert.Single(PmgLocatorReader.Read(bytes, Definitions));
        Assert.Equal("speedcam.r", locator.Hookup);
        Assert.True(locator.ReverseDirection);
        Assert.Equal(20, locator.MeasurementDistance);
        Assert.Equal(new Vector3(8, 0, 2), locator.Position);
        Assert.Equal(Quaternion.Identity, locator.Rotation);
    }

    [Fact]
    public void DecorativeOrUnknownHookupsAreNotCameras()
    {
        Assert.Empty(PmgLocatorReader.Read(Model("flare.vehicle"), Definitions));
    }

    [Fact]
    public void UnsupportedVersionsFailInsteadOfInventingPlacements()
    {
        var bytes = Model("speedcam.20");
        bytes[0] = 0x16;
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(bytes, Definitions));
    }

    [Fact]
    public void OutOfBoundsLocatorSectionIsRejected()
    {
        var bytes = Model("speedcam.20");
        PutInt(bytes, 20, int.MaxValue);
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(bytes, Definitions));
    }

    [Fact]
    public void HookupCannotEscapeStringPool()
    {
        var bytes = Model("speedcam.20");
        PutInt(bytes, 112 + 40, 999);
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(bytes, Definitions));
    }

    [Fact]
    public void UnterminatedHookupIsRejected()
    {
        var bytes = Model("speedcam.20");
        bytes[^1] = (byte)'x';
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(bytes, Definitions));
    }

    [Fact]
    public void NonFinitePlacementIsRejected()
    {
        var bytes = Model("speedcam.20");
        PutFloat(bytes, 112 + 8, float.NaN);
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(bytes, Definitions));
    }

    [Fact]
    public void NegativeSectorCoordinatesUseFloorAcrossOrigin()
    {
        var sectors = NearbyCameraScanner.SectorsNear(new Vector3(-1, 0, -1), 2);
        Assert.Contains(new TruckLib.ScsMap.SectorCoordinate(-1, -1), sectors);
        Assert.Contains(new TruckLib.ScsMap.SectorCoordinate(0, 0), sectors);
        Assert.Equal(4, sectors.Length);
    }

    [Fact]
    public void InvalidSectorSelectionCannotEnumerateUnboundedCoordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NearbyCameraScanner.SectorsNear(new Vector3(float.PositiveInfinity, 0, 0), 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearbyCameraScanner.SectorsNear(Vector3.Zero, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearbyCameraScanner.SectorsNear(new Vector3(float.MaxValue, 0, 0), 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearbyCameraScanner.SectorsNear(Vector3.Zero, -1));
    }

    [Fact]
    public void TypedSiiFloatAndBooleanAttributesAreAccepted()
    {
        Assert.Equal((20f, true), NearbyCameraScanner.ReadDefinitionValues(20f, true));
        Assert.Equal((20f, false), NearbyCameraScanner.ReadDefinitionValues("20.0", "false"));
        Assert.Throws<InvalidDataException>(() => NearbyCameraScanner.ReadDefinitionValues(float.NaN, false));
        Assert.Throws<InvalidDataException>(() => NearbyCameraScanner.ReadDefinitionValues(-1f, false));
    }

    [Fact]
    public void StreamingReaderSkipsGeometryAndRestoresCallerPosition()
    {
        var bytes = Model("speedcam.20");
        Array.Resize(ref bytes, 1_000_000);
        using var stream = new CountingStream(bytes);
        stream.Position = 7;
        Assert.Single(PmgLocatorReader.Read(stream, Definitions));
        Assert.Equal(7, stream.Position);
        Assert.True(stream.BytesRead < 200, $"Read {stream.BytesRead} bytes rather than only locator metadata.");
    }

    [Fact]
    public void StreamingReaderBoundsMetadataBeforeAllocation()
    {
        var bytes = Model("speedcam.20");
        PutInt(bytes, 92, 17 * 1024 * 1024);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => PmgLocatorReader.Read(stream, Definitions));
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            int count = base.Read(buffer);
            BytesRead += count;
            return count;
        }
    }

    [Fact]
    public void NativeQuaternionStorageIsWxyz()
    {
        var bytes = Model("speedcam.20");
        PutFloat(bytes, 112 + 24, 0);
        PutFloat(bytes, 112 + 32, 1);
        var camera = Assert.Single(PmgLocatorReader.Read(bytes, Definitions));
        Assert.Equal(new Quaternion(0, 1, 0, 0), camera.Rotation);
    }

    [Fact]
    public void StreamingReaderPreservesPartMembershipForVariantFiltering()
    {
        var original = Model("speedcam.20");
        var bytes = new byte[original.Length + 24];
        original.AsSpan(0, 112).CopyTo(bytes);
        original.AsSpan(112).CopyTo(bytes.AsSpan(136));
        PutInt(bytes, 8, 1);
        PutInt(bytes, 76, 112);
        PutInt(bytes, 80, 136);
        PutInt(bytes, 88, 180);
        PutInt(bytes, 112 + 16, 1);
        PutInt(bytes, 112 + 20, 0);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(0, Assert.Single(PmgLocatorReader.Read(stream, Definitions)).PartIndex);
    }

    private static byte[] Model(string hookup)
    {
        byte[] name = Encoding.UTF8.GetBytes(hookup + "\0");
        var bytes = new byte[156 + name.Length];
        bytes[0] = 0x15; bytes[1] = (byte)'g'; bytes[2] = (byte)'m'; bytes[3] = (byte)'P';
        PutInt(bytes, 20, 1);
        PutInt(bytes, 80, 112);
        PutInt(bytes, 88, 156);
        PutInt(bytes, 92, name.Length);
        PutFloat(bytes, 112 + 8, 8);
        PutFloat(bytes, 112 + 16, 2);
        PutFloat(bytes, 112 + 20, 1);
        PutFloat(bytes, 112 + 24, 1);
        name.CopyTo(bytes, 156);
        return bytes;
    }

    private static void PutInt(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void PutFloat(byte[] bytes, int offset, float value) =>
        PutInt(bytes, offset, BitConverter.SingleToInt32Bits(value));
}
