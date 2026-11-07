using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class FinlandCalibrationTests
{
    private static CameraCalibrationSet Profile()
    {
        using var stream = typeof(FinlandCalibrationTests).Assembly.GetManifestResourceStream("FinlandCalibration.json")!;
        using var reader = new StreamReader(stream);
        return CameraCalibrationSet.Parse(reader.ReadToEnd());
    }

    private static CameraObservation Observation() => new("299b94880f010010:0",
        new(42970.4f, 11.9453125f, -55600.184f), Vector3.Zero, Vector3.Zero, false,
        "/model2/speedcamera/balt/speedcamera_01_fi.pmd", "speedcam.20")
    {
        LocatorName = "0", ModelVariant = "default", ActivePartVerified = true,
        MetadataFingerprint = "81B0BA29911A5EAF00426E16743785D3BC0107FBE990436B126E04BCC165273E",
        LocalToWorld = new(-.9997661f, 0, .021642905f, 0,
            0, 1, 0, 0, -.021642905f, 0, -.9997661f, 0, 0, 0, 0, 1),
        MeasurementDistanceMeters = 20
    };

    [Fact]
    public void PreservedFinePoseSupportsLocalAxisAndOffsetWithinTriggerRadius()
    {
        var raw = Observation();
        var result = Profile().Apply(raw);
        var sampledFine = new Vector3(42972.7f, 12, -55573);
        var heading = new Vector3(-(float)Math.Sin(.02 * Math.Tau), 0, -(float)Math.Cos(.02 * Math.Tau));
        Assert.True(result.DirectionVerified);
        Assert.InRange(Vector3.Distance(sampledFine, result.MonitoredPosition), 7.3f, 7.6f);
        Assert.InRange(Vector3.Dot(heading, result.TravelDirection), .994f, .996f);
        Assert.InRange(Vector3.Distance(raw.LocatorPosition, sampledFine), 27, 28);
        // The fine is evidence of a monitoring region, not an exact trigger center.
        Assert.NotEqual(sampledFine, result.MonitoredPosition);
    }

    [Fact]
    public void GeometryWarningsRespectApproachAndRejectOppositeTravel()
    {
        var camera = Profile().Apply(Observation());
        var monitor = new MonitoredCamera(camera.Id, camera.MonitoredPosition, camera.TravelDirection);
        var direction = camera.TravelDirection;
        DirectedRoad road = new("test-road", 1, 2,
            [camera.MonitoredPosition - direction * 800, camera.MonitoredPosition + direction * 100]);
        foreach (int distance in new[] { 500, 200 })
        {
            var position = camera.MonitoredPosition - direction * distance;
            var match = new DirectedPathMatcher().Match(position, direction, monitor, [road], true);
            Assert.Equal(CameraApproach.RouteAhead, match.Approach);
            Assert.InRange(match.RoadDistanceMeters!.Value, distance - .02, distance + .02);
            Assert.Equal(CameraApproach.Unknown,
                new DirectedPathMatcher().Match(position, -direction, monitor, [road], false).Approach);
        }
    }

    [Fact]
    public void ChangedFingerprintOrDisabledVariantRemainsUnverified()
    {
        var profile = Profile();
        Assert.False(profile.Apply(Observation() with { MetadataFingerprint = new string('A', 64) }).DirectionVerified);
        Assert.False(profile.Apply(Observation() with { ModelVariant = "def_no_trig" }).DirectionVerified);
        Assert.False(profile.Apply(Observation() with { ActivePartVerified = false }).DirectionVerified);
    }
}
