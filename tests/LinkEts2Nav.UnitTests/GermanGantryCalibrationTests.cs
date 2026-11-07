using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class GermanGantryCalibrationTests
{
    private static CameraCalibrationSet Profile()
    {
        using var stream=typeof(GermanGantryCalibrationTests).Assembly.GetManifestResourceStream("GermanGantryCalibration.json")!;
        using var reader=new StreamReader(stream);
        return CameraCalibrationSet.Parse(reader.ReadToEnd());
    }

    private static CameraObservation First() => new("2cb8aabca28d0000:0",
        new(-2107.587f,58.65625f,-4373.443f),Vector3.Zero,Vector3.Zero,false,
        "/model/sign/traffic/de/3lane_speedlimit_100_no_overtaking_trucks_60_sc.pmd","speedcam.20")
    {
        LocatorName="0",ModelVariant="default",ActivePartVerified=true,
        MetadataFingerprint="44A97F000788032A63A000E81CBB4D7664F349A3DCBC1C197DF3F064F4FECA85",
        LocalToWorld=new(.97886765f,0,-.20449592f,0,0,1,0,0,.20449592f,0,.97886765f,0,0,0,0,1),
        MeasurementDistanceMeters=20
    };

    private static CameraObservation Opposite() => First() with
    {
        Id="2cb8a94806cd0001:0",LocatorPosition=new(-1913.8525f,59.26953f,-4000.8745f),
        LocalToWorld=new(-.7095707f,0,.7046343f,0,0,1,0,0,-.7046343f,0,-.7095707f,0,0,0,0,1)
    };

    [Fact]
    public void PositiveFinePoseFitsMonitoringVicinityAndTravelDirection()
    {
        var c=Profile().Apply(First());
        var fine=new Vector3(-2111.2f,61.1f,-4399);
        var heading=new Vector3(-(float)Math.Sin(.51949*Math.Tau),0,-(float)Math.Cos(.51949*Math.Tau));
        Assert.True(c.DirectionVerified);
        Assert.InRange(Vector3.Distance(fine,c.MonitoredPosition),6.4f,6.6f);
        Assert.True(Vector3.Dot(heading,c.TravelDirection)>.99f);
        Assert.NotEqual(fine,c.MonitoredPosition);
    }

    [Fact]
    public void IndependentOppositeFacingInstanceMatchesObservedHeadingButWasOutsideTriggerRadius()
    {
        var c=Profile().Apply(Opposite());
        var closest=new Vector3(-1910.339f,60.808167f,-3973.3184f);
        var heading=new Vector3(-(float)Math.Sin(.6405970454*Math.Tau),0,-(float)Math.Cos(.6405970454*Math.Tau));
        Assert.True(c.DirectionVerified);
        Assert.InRange(Vector3.Dot(heading,c.TravelDirection),-.996f,-.993f);
        Assert.InRange(Vector3.Distance(closest,c.MonitoredPosition),17,17.3f);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(200)]
    public void MatchingTravelCanWarnButOppositeTravelCannotBorrowSameDirectedPath(int distance)
    {
        var c=Profile().Apply(First());
        var road=new DirectedRoad("test",1,2,
            [c.MonitoredPosition-c.TravelDirection*800,c.MonitoredPosition+c.TravelDirection*100]);
        var position=c.MonitoredPosition-c.TravelDirection*distance;
        var camera=new MonitoredCamera(c.Id,c.MonitoredPosition,c.TravelDirection);
        var matcher=new DirectedPathMatcher();
        var yes=matcher.Match(position,c.TravelDirection,camera,[road],true);
        Assert.Equal(CameraApproach.RouteAhead,yes.Approach);
        Assert.InRange(yes.RoadDistanceMeters!.Value,distance-.02,distance+.02);
        Assert.Equal(CameraApproach.Unknown,matcher.Match(position,-c.TravelDirection,camera,[road],false).Approach);
    }

    [Fact]
    public void ChangedResourceDisabledPartOrOtherVariantCannotReuseCalibration()
    {
        var set=Profile();
        Assert.False(set.Apply(First() with { MetadataFingerprint=new string('A',64) }).DirectionVerified);
        Assert.False(set.Apply(First() with { ActivePartVerified=false }).DirectionVerified);
        Assert.False(set.Apply(First() with { ModelVariant="def_no_trig" }).DirectionVerified);
    }
}
