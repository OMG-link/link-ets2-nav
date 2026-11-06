using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class DirectedPathMatcherTests
{
    private static readonly Vector3 Forward = Vector3.UnitX;
    private static Vector3 Point(float x, float z = 0, float y = 0) => new(x, y, z);
    private static DirectedRoad Road(params Vector3[] points) => new("road", 1, 2, points);
    private static MonitoredCamera Camera(Vector3 position, Vector3? heading = null) =>
        new("camera", position, heading ?? Forward);

    [Fact]
    public void CurvedPathUsesRoadLengthRatherThanChord()
    {
        var road = Road(Point(0), Point(200), Point(200, 200));
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(200, 150), Vector3.UnitZ), [road], true);
        Assert.Equal(CameraApproach.RouteAhead, match.Approach);
        Assert.Equal(300, match.RoadDistanceMeters!.Value, 3);
        Assert.True(match.EuclideanDistanceMeters < 220);
    }

    [Fact]
    public void ContinuousRoadChainAccumulatesDistance()
    {
        DirectedRoad[] roads = [new("a", 1, 2, [Point(0), Point(200)]),
            new("b", 2, 3, [Point(200), Point(500)])];
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), roads, true);
        Assert.Equal(CameraApproach.RouteAhead, match.Approach);
        Assert.Equal(300, match.RoadDistanceMeters!.Value, 3);
    }

    [Fact]
    public void ExplicitForcedContinuationSupportsNoNavigation()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(350)), [Road(Point(0), Point(500))], false);
        Assert.Equal(CameraApproach.UnavoidableAhead, match.Approach);
    }

    [Fact]
    public void CameraBehindVehicleHasSignedNegativeDistance()
    {
        var match = new DirectedPathMatcher().Match(Point(350), Forward,
            Camera(Point(50)), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Behind, match.Approach);
        Assert.Equal(-300, match.RoadDistanceMeters!.Value, 3);
    }

    [Fact]
    public void ReverseTravelWorksWhenPolylineAndUidsAreReversed()
    {
        var road = new DirectedRoad("reverse", 2, 1, [Point(500), Point(0)]);
        var match = new DirectedPathMatcher().Match(Point(400), -Forward,
            Camera(Point(100), -Forward), [road], true);
        Assert.Equal(CameraApproach.RouteAhead, match.Approach);
        Assert.Equal(300, match.RoadDistanceMeters!.Value, 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongVehicleOrCameraDirectionIsNotEvidence(bool wrongVehicle)
    {
        var match = new DirectedPathMatcher().Match(Point(50), wrongVehicle ? -Forward : Forward,
            Camera(Point(350), wrongVehicle ? Forward : -Forward), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void NearbyParallelRoadOutsideProjectionToleranceIsRejected()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(350, 20)), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void StackedRoadOutsideVerticalToleranceIsRejected()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(350, y: 10)), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void OverlappingPathOccurrencesAreAmbiguous()
    {
        var road = Road(Point(0), Point(500), Point(500, 100), Point(0, 100), Point(0), Point(500));
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), [road], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void SimilarParallelReturnOccurrencesAreAmbiguous()
    {
        var road = Road(Point(0), Point(500), Point(500, 100), Point(0, 100), Point(0, 1), Point(500, 1));
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), [road], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NodeMismatchOrPhysicalGapDoesNotInventConnection(bool mismatchNode)
    {
        DirectedRoad[] roads = [new("a", 1, 2, [Point(0), Point(200)]),
            new("b", mismatchNode ? 99UL : 2UL, 3, [Point(mismatchNode ? 200 : 210), Point(500)])];
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), roads, true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void SmallNormalPolylineSegmentsDoNotProduceFalseAmbiguity()
    {
        var road = Road(Enumerable.Range(0, 101).Select(i => Point(i * 5)).ToArray());
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), [road], true);
        Assert.Equal(CameraApproach.RouteAhead, match.Approach);
        Assert.Equal(300, match.RoadDistanceMeters!.Value, 3);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidMapCoordinatesAreRejected(float coordinate)
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(350)), [Road(Point(0), Point(coordinate))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void MissingCameraDirectionIsNotInferredFromVehicleDirection()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(350), Vector3.Zero), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void EmptyPathCannotConfirmCamera()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward, Camera(Point(350)), [], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }
    [Fact]
    public void MonitoringPositionBeyondPathEndCannotBeProjectedBackOntoRoute()
    {
        var match = new DirectedPathMatcher().Match(Point(50), Forward,
            Camera(Point(505)), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void VehicleBeyondPathStartCannotBeProjectedForwardOntoRoute()
    {
        var match = new DirectedPathMatcher().Match(Point(-5), Forward,
            Camera(Point(350)), [Road(Point(0), Point(500))], true);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }}
