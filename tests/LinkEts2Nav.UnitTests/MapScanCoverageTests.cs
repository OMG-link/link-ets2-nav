using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class MapScanCoverageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static (TruckLib.ScsMap.Map, RouteSnapshot) Scene()
    {
        var map = new TruckLib.ScsMap.Map();
        Vector3[] points = [new(0,0,0), new(0,0,3000), new(3000,0,3000), new(7000,0,3000)];
        float[] distances = [10000,7000,4000,0];
        for (int i=0;i<points.Length;i++) map.Nodes.Add((ulong)(i+1),new Node { Uid=(ulong)(i+1), Position=points[i] });
        return (map, RouteSnapshot.Parse(Enumerable.Range(0,4).Select(i=>new RouteNode((ulong)(i+1),distances[i],0)),Now));
    }
    [Fact]
    public void FollowsBentRouteAndClipsAtFiveKilometersInsteadOfUsingEuclideanRadius()
    {
        var (host, route) = Scene();
        var coverage = MapScanCoverage.Create(default,1100,host,route,10000);
        Assert.True(coverage.Contains(new(1900,0,3000)));
        Assert.False(coverage.Contains(new(2400,0,3000)));
        Assert.False(coverage.Contains(new(3000,0,0)));
        Assert.Equal(2, coverage.RouteSegments.Count);
        Assert.DoesNotContain(coverage.Sectors,s=>s.X==1);
    }
    [Fact]
    public void LongEdgeLoadsForwardSectorButNotEquallyDistantSidewaysSector()
    {
        var host=new TruckLib.ScsMap.Map();
        host.Nodes.Add(1,new Node{Uid=1,Position=Vector3.Zero});
        host.Nodes.Add(2,new Node{Uid=2,Position=new(0,0,10000)});
        var route=RouteSnapshot.Parse([new(1,10000,0),new(2,0,0)],Now);
        var coverage=MapScanCoverage.Create(default,1100,host,route,10000);
        Assert.Contains(coverage.Sectors,s=>s.Z==1);
        Assert.DoesNotContain(coverage.Sectors,s=>s.X==1);
        Assert.True(coverage.Contains(new(0,0,4999)));
        Assert.False(coverage.Contains(new(0,0,5201)));
    }

    [Fact]
    public void RemainingDistanceAdvancesWindowAndPreservesNearbyDisk()
    {
        var (host,route)=Scene();
        var coverage=MapScanCoverage.Create(new(0,0,2000),1100,host,route,8000);
        Assert.True(coverage.Contains(new(3900,0,3000)));
        Assert.False(coverage.Contains(new(4400,0,3000)));
        Assert.True(coverage.Contains(new(-1000,0,2000)));
        Assert.False(coverage.Contains(default));
    }
    [Fact]
    public void NoRouteOrUnresolvableNodesKeepsOriginalNearbyCoverage()
    {
        var (host,route)=Scene();
        foreach(var r in new[]{null, route with{Availability=RouteAvailability.Stale},route with{Nodes=[new(999,10000,0)]}})
        {
            var coverage=MapScanCoverage.Create(default,1100,host,r,10000);
            Assert.Empty(coverage.RouteSegments);
            Assert.Equal(NearbyCameraScanner.SectorsNear(default,1200),coverage.Sectors);
        }
    }
    [Fact]
    public void ScaledOfficialMileageDoesNotShrinkWorldSpaceHorizon()
    {
        var (host,route)=Scene();
        route=route with{Nodes=route.Nodes.Select(n=>n with{DistanceToEndMeters=n.DistanceToEndMeters*19}).ToArray()};
        var coverage=MapScanCoverage.Create(default,1100,host,route,190000);
        Assert.True(coverage.Contains(new(1900,0,3000)));
        Assert.False(coverage.Contains(new(2400,0,3000)));
    }

    [Fact]
    public void ReversePayloadOrderAndMissingIntermediateUidStillUseKnownRouteNodes()
    {
        var (host,route)=Scene();
        route=route with{Nodes=route.Nodes.Append(new(999,8500,0)).Reverse().ToArray()};
        var coverage=MapScanCoverage.Create(default,1100,host,route,10000);
        Assert.True(coverage.Contains(new(1900,0,3000)));
        Assert.False(coverage.Contains(new(2400,0,3000)));
    }
}
