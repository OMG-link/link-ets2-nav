using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib;
using PrefabDescriptor = TruckLib.Models.Ppd.PrefabDescriptor;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationRouteMapTests
{
    [Fact]
    public void FullRoutePortAndTenKilometreCorridorMatchTheUnfilteredMap()
    {
        using var files = new Fixture();
        var source = new NativeMap { NormalScale = 21, CityScale = 4 };
        var first = Road.Add(source, new(10, 2, 10), new(860, 3, 30), "local");
        var roads = new List<Road> { first };
        for (int i = 2; i <= 26; i++) roads.Add(roads[^1].Append(new(10 + i * 850, i, 10 + (i % 3) * 20)));
        first.Left.Variant = "narrow";
        first.Left.TrafficRule = "restricted";
        first.Left.VariantOverrides.Add(new("bus", 2, 7));
        var portRoad = roads[^1];
        var descriptor = new PrefabDescriptor { Nodes = [new() { Position = Vector3.Zero, Direction = -Vector3.UnitZ }] };
        var prefab = Prefab.Add(source, portRoad.ForwardNode.Position, "terminal", descriptor);
        source.Nodes.Remove(prefab.Nodes[0].Uid);
        prefab.Nodes[0] = portRoad.ForwardNode;
        portRoad.ForwardNode.ForwardItem = prefab;
        var ferry = Ferry.Add(source, new(20, 2, 40), "port_a");
        ferry.Prefab = prefab;
        prefab.Ferry = ferry;
        var city = CityArea.Add(source, new(20, 0, 50), "city_a", 120, 240);
        var sign = Sign.Add(source, new(50, 0, 60), "sign");
        var model = Model.Add(source, new(55, 0, 60), "scenery", default, default);
        source.Save(files.Directory, "test");

        var token = TestContext.Current.CancellationToken;
        using var index = NavigationSectorIndex.OpenOrBuild(files, files.Path, Path.Combine(files.Directory, "index"), token);
        var route = RouteSnapshot.Parse(roads.Select(r => r.Node.Uid).Append(portRoad.ForwardNode.Uid)
            .Select((uid, i) => new RouteNode(uid, (roads.Count - i) * 18000, 0)), DateTimeOffset.UtcNow);
        var complete = NativeMap.Open(files.Path, new DiskFileSystem());
        var actual = NavigationRouteMap.ReadRoute(files, files.Path, index, route, token);
        Assert.Equal(complete.NormalScale, actual.NormalScale);
        Assert.Equal(complete.CityScale, actual.CityScale);
        Assert.Equal(complete.EditorMapId, actual.EditorMapId);
        Assert.All(actual.Sectors.Values, sector => Assert.Same(actual, sector.Map));
        Assert.False(actual.MapItems.ContainsKey(sign.Uid));
        Assert.False(actual.Nodes.ContainsKey(model.Node.Uid));
        Assert.DoesNotContain(city.Uid, actual.MapItems.Keys);
        Assert.DoesNotContain(ferry.Uid, actual.MapItems.Keys);
        foreach (var original in complete.MapItems.Values.OfType<Road>())
        {
            var road = Assert.IsType<Road>(actual.MapItems[original.Uid]);
            Assert.Same(actual, road.Node.Parent);
            Assert.Same(actual.Nodes[original.Node.Uid], road.Node);
            Assert.Same(actual.Nodes[original.ForwardNode.Uid], road.ForwardNode);
            Assert.Equal(original.InterpolateCurve(.35f), road.InterpolateCurve(.35f));
            Assert.Equal(original.Left.TrafficRule, road.Left.TrafficRule);
            Assert.Equal(original.Left.Variant, road.Left.Variant);
            Assert.Equal(original.Left.VariantOverrides, road.Left.VariantOverrides);
            Assert.Equal(original.Node.BackwardItem?.Uid, road.Node.BackwardItem?.Uid);
            Assert.Equal(original.Node.ForwardItem?.Uid, road.Node.ForwardItem?.Uid);
        }
        Assert.Same(((Road)actual.MapItems[first.Uid]).Right, ((Road)actual.MapItems[portRoad.Uid]).Right);
        var terminals = NavigationRouteMap.ReadFerries(files, files.Path, index, token);
        var resultPort = Assert.IsType<Ferry>(terminals.MapItems[ferry.Uid]);
        var resultPrefab = Assert.IsType<Prefab>(terminals.MapItems[prefab.Uid]);
        Assert.Same(resultPrefab, resultPort.Prefab);
        Assert.Same(resultPort, resultPrefab.Ferry);
        Assert.Same(terminals.Nodes[portRoad.ForwardNode.Uid], resultPrefab.Nodes[0]);
        var ports = new FerryJourneyIndex(terminals, []);
        Assert.True(ports.TryPortPassage(resultPort.Node.Uid, resultPrefab.Nodes[0].Uid, out _));

        var expected = new RouteJourneyEstimator(complete, (_, _) => new(80, "category")).Estimate(route);
        Assert.True(expected.Ready, expected.Failure);
        Assert.Equal(expected, new RouteJourneyEstimator(actual, (_, _) => new(80, "category")).Estimate(route));
        MapScanCoverage Corridor(NativeMap map) => MapScanCoverage.Create(first.Node.Position, 1100, map, route, roads.Count * 18000);
        var corridor = Corridor(actual);
        Assert.Equal(Corridor(complete).RouteSegments, corridor.RouteSegments);
        Assert.InRange(corridor.RouteSegments.Sum(s => (double)Vector3.Distance(s.From, s.To)), 9999.9, 10000.1);

    }

    [Fact]
    public void ARouteAndAReverseLinkedPortKeepOnlyTheirNativeEndpointsAndAdjacentRoads()
    {
        using var files = new Fixture();
        var source = new NativeMap();
        var approach = Road.Add(source, new(3630, 2, 130), new(3975, 2, 130), "approach");
        var model = new PrefabDescriptor { Nodes = [
            new() { Position = Vector3.Zero, Direction = -Vector3.UnitZ, InputLines = [0], OutputLines = [1] },
            new() { Position = new(125, 0, 0), Direction = Vector3.UnitZ }
        ], NavCurves = [
            new() { StartPosition = Vector3.Zero, EndPosition = Vector3.UnitZ, Length = 10, NextLines = { 1 } },
            new() { StartPosition = Vector3.UnitZ, EndPosition = Vector3.Zero, Length = 10 }
        ] };
        var prefab = Prefab.Add(source, approach.ForwardNode.Position, "terminal", model);
        source.Nodes.Remove(prefab.Nodes[0].Uid);
        prefab.Nodes[0] = approach.ForwardNode;
        approach.ForwardNode.ForwardItem = prefab;

        var exit = Road.Add(source, prefab.Nodes[1].Position, new(12500, 8, 350), "exit");
        source.Nodes.Remove(exit.Node.Uid);
        exit.Node = prefab.Nodes[1];
        exit.Node.BackwardItem = prefab;
        exit.Node.ForwardItem = exit;
        var next = exit.Append(new(13600, 7, 300));
        var ferry = Ferry.Add(source, new(21000, 3, 130), "real_port");
        ferry.Prefab = new Prefab { Uid = 0 }; // TruckLib serializes the absent link as a zero UID.
        prefab.Ferry = ferry;
        Road.Add(source, new(3600, 2, 137), new(3975, 2, 137), "parallel");
        Model.Add(source, new(56000, 0, 500), "distant", default, default);
        source.Save(files.Directory, "test");

        var token = TestContext.Current.CancellationToken;
        using var index = NavigationSectorIndex.OpenOrBuild(files, files.Path, Path.Combine(files.Directory, "index"), token);
        var route = RouteSnapshot.Parse([
            new(approach.Node.Uid, 710, 0), new(approach.ForwardNode.Uid, 365, 0),
            new(approach.ForwardNode.Uid, 345, 0), new(approach.Node.Uid, 0, 0)
        ], DateTimeOffset.UnixEpoch);
        files.Opens.Clear();
        var routed = NavigationRouteMap.ReadRoute(files, files.Path, index, route, token);
        Assert.Equal(new[] { approach.Uid, prefab.Uid, exit.Uid }.Order(), routed.MapItems.Keys.Order());
        Assert.DoesNotContain(files.SectorFile(14, "base"), files.Opens.Keys);
        Assert.DoesNotContain(files.SectorFile(5, "base"), files.Opens.Keys);
        CheckEndpoints(routed);
        var routedPrefab = Assert.IsType<Prefab>(routed.MapItems[prefab.Uid]);
        Assert.Equal(ferry.Uid, routedPrefab.Ferry.Uid);
        Assert.IsNotType<Ferry>(routedPrefab.Ferry);
        var last = Assert.IsType<Road>(routed.MapItems[exit.Uid]).ForwardNode;
        Assert.Equal(next.Uid, last.ForwardItem.Uid);
        Assert.IsNotType<Road>(last.ForwardItem);

        files.Opens.Clear();
        var ports = NavigationRouteMap.ReadFerries(files, files.Path, index, token);
        Assert.Equal(new[] { approach.Uid, prefab.Uid, exit.Uid, ferry.Uid }.Order(), ports.MapItems.Keys.Order());
        Assert.DoesNotContain(files.SectorFile(14, "base"), files.Opens.Keys);
        CheckEndpoints(ports);
        Assert.Same(ports.MapItems[ferry.Uid], ((Prefab)ports.MapItems[prefab.Uid]).Ferry);

        var complete = NativeMap.Open(files.Path, new DiskFileSystem());
        RouteJourneyEstimate EstimateReturn(NativeMap map) => new RouteJourneyEstimator(map, (_, _) => new(80, "category"),
            prefabLengths: (junction, from, to) => PrefabPassageLengths.Read(junction, model, from, to)).Estimate(route);
        var expectedReturn = EstimateReturn(complete);
        Assert.True(expectedReturn.Ready, expectedReturn.Failure);
        Assert.Equal(expectedReturn, EstimateReturn(routed));
        var expected = Assert.Single(new FerryJourneyIndex(complete, [], roadHalfWidth: _ => 3).Ports);
        var actual = Assert.Single(new FerryJourneyIndex(ports, [], roadHalfWidth: _ => 3).Ports);
        Assert.Equal(expected.Interfaces, actual.Interfaces);
        Assert.Equal(2, actual.Entrances.Count);
        foreach (var (uid, entry) in actual.Entrances)
        {
            Assert.Equal(expected.Entrances[uid].Position, entry.Position);
            Assert.Equal(expected.Entrances[uid].Inward, entry.Inward);
            Assert.Equal(expected.Entrances[uid].Approach.Points, entry.Approach.Points);
        }

        static void CheckEndpoints(NativeMap map)
        {
            Assert.All(map.MapItems.Values.SelectMany(HostMapView.NodesOf), node =>
            {
                Assert.Same(map, node.Parent);
                Assert.Same(map.Nodes[node.Uid], node);
                foreach (var owner in new[] { node.BackwardItem, node.ForwardItem }.OfType<MapItem>())
                    Assert.Same(map.MapItems[owner.Uid], owner);
            });
        }
    }

    [Fact]
    public void AnExtraNodeClaimingARoadInAnotherBatchStillFailsNativeValidation()
    {
        using var files = new Fixture();
        var source = new NativeMap();
        for (int i = 1; i < 6; i++) CityArea.Add(source, new(i * 4000 + 15, 0, 10), "city", 60, 60);
        var road = Road.Add(source, new(28010, 0, 20), new(28110, 0, 20), "local");
        var unrelated = Model.Add(source, new(15, 0, 20), "decor", default, default);
        unrelated.Node.ForwardItem = road;
        source.Save(files.Directory, "test");
        var error = Assert.Throws<InvalidDataException>(() => NavigationSectorIndex.OpenOrBuild(files, files.Path,
            Path.Combine(files.Directory, "index"), TestContext.Current.CancellationToken));
        Assert.Contains(road.Uid.ToString("X16"), error.Message);
        Assert.Contains(unrelated.Node.Uid.ToString("X16"), error.Message);
    }

    [Fact]
    public void ARouteEndpointCanLiveInAnUnrelatedSectorsSceneryFile()
    {
        using var files = new Fixture();
        var source = new NativeMap();
        var scenery = Model.Add(source, new(15, 2, 30), "scenery", default, default);
        for (int i = 1; i < 6; i++) CityArea.Add(source, new(i * 4000 + 15, 0, 10), "city", 60, 60);
        var road = Road.Add(source, new(24020, 1, 40), new(24090, 1, 40), "route");
        source.Save(files.Directory, "test");

        // The stock writer groups by position. Move only the two physical rows between native
        // files, leaving both objects, their geometry and all reciprocal UID references intact.
        var roadRow = Serialize((Node)road.Node);
        var sceneryRow = Serialize((Node)scenery.Node);
        ReplaceRow(files.SectorFile(6, "base"), roadRow, sceneryRow);
        ReplaceRow(files.SectorFile(0, "aux"), sceneryRow, roadRow);

        var full = NativeMap.Open(files.Path, new DiskFileSystem());
        var token = TestContext.Current.CancellationToken;
        using var index = NavigationSectorIndex.OpenOrBuild(files, files.Path, Path.Combine(files.Directory, "index"), token);
        Assert.True(index.TryGetNode(road.Node.Uid, out var physical));
        Assert.Equal(new SectorCoordinate(0, 0), physical.Sector);
        files.Opens.Clear();
        var route = RouteSnapshot.Parse([new(road.Node.Uid, 70, 0), new(road.ForwardNode.Uid, 0, 0)], DateTimeOffset.UnixEpoch);
        var actual = NavigationRouteMap.ReadRoute(files, files.Path, index, route, token);
        var loaded = Assert.IsType<Road>(Assert.Single(actual.MapItems).Value);
        Assert.Equal(((Road)full.MapItems[road.Uid]).InterpolateCurve(.5f), loaded.InterpolateCurve(.5f));
        Assert.Same(actual.Nodes[road.Node.Uid], loaded.Node);
        Assert.Same(actual, loaded.Node.Parent);
        Assert.Same(loaded, loaded.Node.ForwardItem);
        Assert.DoesNotContain(scenery.Node.Uid, actual.Nodes.Keys);
        Assert.Equal(1, files.Opens[files.SectorFile(0, "aux")]);
        Assert.Equal(1, files.Opens[files.SectorFile(6, "base")]);
        Assert.DoesNotContain(files.SectorFile(3, "base"), files.Opens.Keys);

        static byte[] Serialize(Node node)
        {
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            node.Serialize(writer);
            return bytes.ToArray();
        }
        static void ReplaceRow(string file, byte[] before, byte[] after)
        {
            byte[] bytes = File.ReadAllBytes(file);
            int offset = bytes.AsSpan().IndexOf(before);
            Assert.True(offset >= 0);
            after.CopyTo(bytes, offset);
            File.WriteAllBytes(file, bytes);
        }
    }

    private sealed class Fixture : IDisposable, IFileSystem
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("nav-base-map-").FullName;
        public string Path => System.IO.Path.Combine(Directory, "test.mbd");
        public Dictionary<string,int> Opens { get; } = [];
        public string SectorFile(int x, string extension) => System.IO.Path.Combine(Directory, "test",
            Sector.SectorFileNameFromSectorCoords(new(x, 0)) + "." + extension);
        public char DirectorySeparator => System.IO.Path.DirectorySeparatorChar;
        public IList<string> GetFiles(string path) => System.IO.Directory.GetFiles(path).Order(StringComparer.Ordinal).ToArray();
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => System.IO.Directory.Exists(path);
        public string GetParent(string path) => System.IO.Path.GetDirectoryName(path)!;
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);
        public Stream Open(string path)
        {
            Opens[path] = Opens.GetValueOrDefault(path) + 1;
            return File.OpenRead(path);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
