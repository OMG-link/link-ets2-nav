using System.Numerics;
using System.Text.Json;
using System.Text.Encodings.Web;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class CameraCalibrationSetTests
{
    [Fact]
    public void MatchingEvidenceTransformsCurrentLocatorRatherThanStoredWorldPoint()
    {
        var calibrations = CameraCalibrationSet.Parse(Json());
        var camera = Observation();
        var result = calibrations.Apply(camera);
        Assert.True(result.DirectionVerified);
        Assert.InRange(Vector3.Distance(new Vector3(10, 20, 10), result.MonitoredPosition), 0, 0.001f);
        Assert.InRange(Vector3.Distance(-Vector3.UnitZ, result.TravelDirection), 0, 0.001f);
        var moved = calibrations.Apply(camera with { LocatorPosition = camera.LocatorPosition + new Vector3(100, 0, 0) });
        Assert.InRange(Vector3.Distance(result.MonitoredPosition + new Vector3(100, 0, 0), moved.MonitoredPosition), 0, 0.001f);
    }

    [Fact]
    public void ChangedMetadataInactivePartsAndDifferentVariantsFailClosed()
    {
        var set = CameraCalibrationSet.Parse(Json());
        Assert.False(set.Apply(Observation() with { MetadataFingerprint = new string('B', 64) }).DirectionVerified);
        Assert.False(set.Apply(Observation() with { ActivePartVerified = false }).DirectionVerified);
        Assert.False(set.Apply(Observation() with { ModelVariant = "def_no_trig" }).DirectionVerified);
        Assert.False(CameraCalibrationSet.Empty.Apply(Observation() with { DirectionVerified = true }).DirectionVerified);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"travelAxis\":\"+Z\"", "\"travelAxis\":\"unknown\"")]
    [InlineData("\"evidence\":\"Test-only synthetic validation\"", "\"evidence\":\"\"")]
    [InlineData("\"schemaVersion\":1", "\"unknown\":1,\"schemaVersion\":1")]
    public void InvalidProfilesAreRejected(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() => CameraCalibrationSet.Parse(Json().Replace(before, after)));
    }

    [Fact]
    public void EmptySchemaHasNoEnabledDefaults()
    {
        Assert.Equal(0, CameraCalibrationSet.Parse("{\"schemaVersion\":1,\"entries\":[]}").Count);
    }

    private static CameraObservation Observation() => new("one", new(10, 20, 30), new(10, 20, 30), Vector3.Zero, false,
        "/model/example.pmd", "speedcam.20")
    {
        LocatorName = "0", ModelVariant = "default", ActivePartVerified = true,
        MetadataFingerprint = new string('A', 64),
        LocalToWorld = Matrix4x4.CreateRotationY(MathF.PI)
    };

    private static string Json() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        entries = new[] { new {
            descriptor = "/model/example.pmd", hookup = "speedcam.20", locator = "0", variant = "default",
            metadataFingerprint = new string('A', 64), travelAxis = "+Z",
            monitoredOffset = new { x = 0, y = 0, z = 20 }, evidence = "Test-only synthetic validation"
        }}
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
