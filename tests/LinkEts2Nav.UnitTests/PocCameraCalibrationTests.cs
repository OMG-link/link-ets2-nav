using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class PocCameraCalibrationTests
{
    private static CameraCalibrationSet Profile()
    {
        using var stream=typeof(PocCameraCalibrationTests).Assembly.GetManifestResourceStream("PocCameraCalibration.json")!;
        using var reader=new StreamReader(stream);
        return CameraCalibrationSet.Parse(reader.ReadToEnd());
    }
    private static CameraObservation Positive() => new("5c505ce18b070001:0",
        new(13505.769f,-1.8242188f,-28933.543f),Vector3.Zero,Vector3.Zero,false,
        "/model2/speedcamera/speed_camera_02_poc.pmd","speedcam.20")
    {
        LocatorName="0",ModelVariant="default",ActivePartVerified=true,
        MetadataFingerprint="7249A8F9162EA2C1292C183C854DB1D6F9A672D9EEE7C0BFBC9CAFA1C0D42EE6",
        LocalToWorld=new(-.9990345f,0,-.04393157f,0,0,1,0,0,.04393157f,0,-.9990345f,0,0,0,0,1),
        MeasurementDistanceMeters=20
    };
    private static CameraObservation Opposite() => Positive() with
    {
        Id="5c505cb545070001:0",LocatorPosition=new(13511.362f,-.84375006f,-28828.514f),
        LocalToWorld=new(.990981f,0,-.13400386f,0,0,1,0,0,.13400386f,0,.990981f,0,0,0,0,1)
    };
    private static Vector3 Heading(double turns)=>new(-(float)Math.Sin(turns*Math.Tau),0,-(float)Math.Cos(turns*Math.Tau));

    [Fact]
    public void PositiveFineFitsEstimatedMonitoringCenterAndActualTravel()
    {
        var camera=Profile().Apply(Positive());
        var fine=new Vector3(13506.8f,-1.8f,-28906.5f);
        Assert.True(camera.DirectionVerified);
        Assert.InRange(Vector3.Distance(fine,camera.MonitoredPosition),7.2f,7.4f);
        Assert.InRange(Vector3.Distance(fine,camera.LocatorPosition),27,28);
        Assert.True(Vector3.Dot(Heading(.00371),camera.TravelDirection)>.99f);
    }

    [Fact]
    public void EarlierIndependentOppositeInstanceWasPassedCloseToEstimatedCenter()
    {
        var camera=Profile().Apply(Opposite());
        var closest=new Vector3(13511.845f,-1.149276f,-28846.934f);
        Assert.True(camera.DirectionVerified);
        Assert.InRange(Vector3.Distance(closest,camera.MonitoredPosition),3.4f,3.6f);
        Assert.True(Vector3.Dot(Heading(.027745761),camera.TravelDirection)<-.99f);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(200)]
    public void SameProfileSupportsMatchingTravelWithoutWarningOppositeTravel(int distance)
    {
        var camera=Profile().Apply(Positive());
        var road=new DirectedRoad("test",1,2,
            [camera.MonitoredPosition-camera.TravelDirection*800,camera.MonitoredPosition+camera.TravelDirection*100]);
        var matcher=new DirectedPathMatcher();
        var target=new MonitoredCamera(camera.Id,camera.MonitoredPosition,camera.TravelDirection);
        var position=camera.MonitoredPosition-camera.TravelDirection*distance;
        var match=matcher.Match(position,camera.TravelDirection,target,[road],true);
        Assert.Equal(CameraApproach.RouteAhead,match.Approach);
        Assert.InRange(match.RoadDistanceMeters!.Value,distance-.02,distance+.02);
        Assert.Equal(CameraApproach.Unknown,matcher.Match(position,-camera.TravelDirection,target,[road],false).Approach);
    }

    [Fact]
    public void ProfileIsBoundToVerifiedPartVariantLocatorAndResourceFingerprint()
    {
        var profile=Profile();
        Assert.Equal(1,profile.Count);
        Assert.False(profile.Apply(Positive() with { ActivePartVerified=false }).DirectionVerified);
        Assert.False(profile.Apply(Positive() with { ModelVariant="def_no_trig" }).DirectionVerified);
        Assert.False(profile.Apply(Positive() with { LocatorName="1" }).DirectionVerified);
        Assert.False(profile.Apply(Positive() with { MetadataFingerprint=new string('A',64) }).DirectionVerified);
    }
}
