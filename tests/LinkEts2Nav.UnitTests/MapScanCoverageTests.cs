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
        Vector3[] points = [new(0,0,0), new(0,0,3000), new(3000,0,3000), new(12000,0,3000)];
        float[] distances = [15000,12000,9000,0];
        for (int i=0;i<points.Length;i++) map.Nodes.Add((ulong)(i+1),new Node { Uid=(ulong)(i+1), Position=points[i] });
        return (map, RouteSnapshot.Parse(Enumerable.Range(0,4).Select(i=>new RouteNode((ulong)(i+1),distances[i],0)),Now));
    }
    [Fact]
    public void FollowsBentRouteAndClipsAtTenKilometersInsteadOfUsingEuclideanRadius()
    {
        var (host, route) = Scene();
        var coverage = MapScanCoverage.Create(default,1100,host,route,15000);
        Assert.True(coverage.Contains(new(6999,0,3000)));
        Assert.False(coverage.Contains(new(7201,0,3000)));
        Assert.False(coverage.Contains(new(3000,0,0)));
        Assert.Equal(3, coverage.RouteSegments.Count);
        Assert.Equal(10000,coverage.RouteSegments.Sum(s=>(double)Vector3.Distance(s.From,s.To)),2);
        Assert.Contains(coverage.Sectors,s=>s.X==1);
        Assert.DoesNotContain(coverage.Sectors,s=>s.X==2);
    }
    [Fact]
    public void LongEdgeLoadsForwardSectorButNotEquallyDistantSidewaysSector()
    {
        var host=new TruckLib.ScsMap.Map();
        host.Nodes.Add(1,new Node{Uid=1,Position=Vector3.Zero});
        host.Nodes.Add(2,new Node{Uid=2,Position=new(0,0,10000)});
        var route=RouteSnapshot.Parse([new(1,10000,0),new(2,0,0)],Now);
        var coverage=MapScanCoverage.Create(default,1100,host,route,10000);
        Assert.Contains(coverage.Sectors,s=>s.Z==2);
        Assert.DoesNotContain(coverage.Sectors,s=>s.X==1);
        Assert.True(coverage.Contains(new(0,0,10000)));
        Assert.False(coverage.Contains(new(0,0,10201)));
        Assert.Equal(8,coverage.Sectors.Count);
    }

    [Fact]
    public void RemainingDistanceAdvancesWindowAndPreservesNearbyDisk()
    {
        var (host,route)=Scene();
        var coverage=MapScanCoverage.Create(new(0,0,2000),1100,host,route,13000);
        Assert.True(coverage.Contains(new(8999,0,3000)));
        Assert.False(coverage.Contains(new(9201,0,3000)));
        Assert.True(coverage.Contains(new(-1000,0,2000)));
        Assert.False(coverage.Contains(default));
    }
    [Fact]
    public void NoRouteOrUnresolvableNodesKeepsOriginalNearbyCoverage()
    {
        var (host,route)=Scene();
        foreach(var r in new[]{null, route with{Availability=RouteAvailability.None},route with{Availability=RouteAvailability.Stale},route with{Nodes=[new(999,10000,0)]}})
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
        var coverage=MapScanCoverage.Create(default,1100,host,route,285000);
        Assert.True(coverage.Contains(new(6999,0,3000)));
        Assert.False(coverage.Contains(new(7201,0,3000)));
    }

    [Fact]
    public void ReversePayloadOrderAndMissingIntermediateUidStillUseKnownRouteNodes()
    {
        var (host,route)=Scene();
        route=route with{Nodes=route.Nodes.Append(new(999,8500,0)).Reverse().ToArray()};
        var coverage=MapScanCoverage.Create(default,1100,host,route,15000);
        Assert.True(coverage.Contains(new(6999,0,3000)));
        Assert.False(coverage.Contains(new(7201,0,3000)));
    }

    [Fact]
    public void ShortRouteStopsAtItsKnownEndRatherThanInventingTenKilometers()
    {
        var (host,route)=Scene();
        var coverage=MapScanCoverage.Create(new(11000,0,3000),1100,host,route,1000);
        Assert.Equal(1000,coverage.RouteSegments.Sum(s=>(double)Vector3.Distance(s.From,s.To)),2);
        Assert.True(coverage.Contains(new(12000,0,3000)));
        Assert.False(coverage.Contains(new(12401,0,3000)));
    }

    [Fact]
    public void SmallAdvancesWithinSameCellsPreserveSectorSetForScannerReuse()
    {
        var (host,route)=Scene();
        var first=MapScanCoverage.Create(default,1100,host,route,15000);
        var second=MapScanCoverage.Create(new(0,0,10),1100,host,route,14990);
        Assert.Equal(first.Sectors,second.Sectors);
        Assert.Equal(second.Sectors.Count,second.Sectors.Distinct().Count());
    }
}
