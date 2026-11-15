using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class MapQueryConsumerTests
{
    private static readonly SectorCoordinate Required = new(8, -3);
    private static MissingMapSectors Missing() => new([Required], "native referenced object owner");
    private static PrefabLanePath Lane(int input) => new(42, 10, 20, input, 0, input, 0, 2, 1,
        [input], [Vector3.Zero, new(0, 0, 20)], 20);

    [Fact]
    public void OriginRetainsTheExactMissingSectorWithoutInventingOneFromTheUid()
    {
        var ending = Lane(1); var main = Lane(0);
        var edge = new DirectedRoad("prefab:42:0:1", 10, 20, ending.Points) { LanePaths = [ending, main] };
        ulong requested = 0;
        var result = new LaneMergeOriginClassifier(new([edge]), node => { requested = node; return Missing() with{QueryAnchor=new(50000,0,0)}; })
            .Classify(ending, 10, main, 10);
        Assert.Equal(10UL, requested);
        Assert.Equal(LaneMergeKind.Uncertain, result.Kind);
        Assert.Equal([Required], Assert.IsType<MissingMapSectors>(result.MapIssue).Sectors);
        Assert.Equal(new Vector3(0,0,10),result.MapIssue!.QueryAnchor);
        var unlocated = new LaneMergeOriginClassifier(new([edge])).Classify(ending, 10, main, 10);
        Assert.Equal(MapQueryFailureKind.UnresolvedReference, Assert.IsType<MapQueryFailure>(unlocated.MapIssue).Kind);
    }

    [Fact]
    public void SlopeGapReturnsDependencyButActualBranchAmbiguityDoesNotRequestMoreMap()
    {
        var gap = new RoadSlopeWindowSampler(new Dictionary<ulong, List<Road>>(), [], _ => Missing()).Sample(10, true);
        Assert.Equal([Required], Assert.IsType<MissingMapSectors>(gap.MapIssue).Sectors);
        var map = new TruckLib.ScsMap.Map();
        var first = Road.Add(map, Vector3.Zero, new(0, 0, 30), default);
        var second = Road.Add(map, Vector3.Zero, new(30, 0, 0), default);
        var called = false;
        var split = new RoadSlopeWindowSampler(new Dictionary<ulong, List<Road>> { [10] = [first, second] }, [],
            _ => { called = true; return Missing(); }).Sample(10, true);
        Assert.False(called);
        Assert.Equal(MapQueryFailureKind.Ambiguous, Assert.IsType<MapQueryFailure>(split.MapIssue).Kind);
    }

    [Fact]
    public void RecursiveSlopeDependencyKeepsItsOriginalSampleInterfaceAnchor()
    {
        var lane=new PrefabLanePath(42,10,20,0,0,0,0,1,1,[0],[Vector3.Zero,new(0,0,10)],10);
        var edge=new DirectedRoad("prefab:42:0:1",10,20,lane.Points){LanePaths=[lane]};
        var sample=new RoadSlopeWindowSampler(new Dictionary<ulong,List<Road>>(),[edge],
            _=>Missing() with{QueryAnchor=new(0,0,10)}).Sample(10,false);
        Assert.Equal(Vector3.Zero,sample.MapIssue!.QueryAnchor);
    }

    [Fact]
    public void SdkConnectionGapSurfacesDependenciesToItsCaller()
    {
        var reported = new List<MapQueryIssue>();
        var source = new RoadPathSource(new(), missingConnection: _ => Missing(), reportMapIssue: reported.Add);
        var route = RouteSnapshot.Parse([new(10, 100, 0), new(20, 0, 0)], DateTimeOffset.UtcNow);
        Assert.Empty(source.GetRouteComponents(route));
        Assert.All(source.MapIssues, issue => Assert.Equal([Required], Assert.IsType<MissingMapSectors>(issue).Sectors));
        Assert.NotEmpty(reported);
        // Cached topology is not a repeated load request on every position tick.
        int count = reported.Count;
        source.GetRouteComponents(route);
        Assert.Equal(count, reported.Count);
    }

    [Fact]
    public void NoNavigationContinuationGapUsesTheSameTypedCollector()
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,Vector3.Zero,new(50,0,0),default);
        var source=new RoadPathSource(map,missingConnection:_=>Missing());
        var none=RouteSnapshot.Parse([],DateTimeOffset.UtcNow);
        var path=source.GetPath(new(10,0,0),Vector3.UnitX,none,RouteAvailability.None);
        Assert.Single(path);
        var dependency=Assert.Single(source.MapIssues.OfType<MissingMapSectors>());
        Assert.Equal([Required],dependency.Sectors);
        Assert.Equal(road.ForwardNode.Position,dependency.QueryAnchor);
    }
}
