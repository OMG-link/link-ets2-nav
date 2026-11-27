using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib;
using PrefabDescriptor = TruckLib.Models.Ppd.PrefabDescriptor;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationBaseMapTests
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

        var complete = NativeMap.Open(files.Path, new DiskFileSystem());
        var actual = NavigationBaseMap.Read(files, files.Path, TestContext.Current.CancellationToken);
        Assert.Equal(complete.NormalScale, actual.NormalScale);
        Assert.Equal(complete.CityScale, actual.CityScale);
        Assert.Equal(complete.EditorMapId, actual.EditorMapId);
        Assert.Equal(complete.Sectors.Keys.OrderBy(s => s.X), actual.Sectors.Keys.OrderBy(s => s.X));
        Assert.All(actual.Sectors.Values, sector => Assert.Same(actual, sector.Map));
        Assert.False(actual.MapItems.ContainsKey(sign.Uid));
        Assert.False(actual.Nodes.ContainsKey(model.Node.Uid));
        Assert.Equal(city.Name, Assert.IsType<CityArea>(actual.MapItems[city.Uid]).Name);
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
        var resultPort = Assert.IsType<Ferry>(actual.MapItems[ferry.Uid]);
        Assert.Same(((Road)actual.MapItems[first.Uid]).Right, ((Road)actual.MapItems[portRoad.Uid]).Right);
        var resultPrefab = Assert.IsType<Prefab>(actual.MapItems[prefab.Uid]);
        Assert.Same(resultPrefab, resultPort.Prefab);
        Assert.Same(resultPort, resultPrefab.Ferry);
        Assert.Same(actual.Nodes[portRoad.ForwardNode.Uid], resultPrefab.Nodes[0]);
        var ports = new FerryJourneyIndex(actual, []);
        Assert.True(ports.TryPortPassage(resultPort.Node.Uid, resultPrefab.Nodes[0].Uid, out _));

        var route = RouteSnapshot.Parse(roads.Select(r => r.Node.Uid).Append(portRoad.ForwardNode.Uid)
            .Select((uid, i) => new RouteNode(uid, (roads.Count - i) * 18000, 0)), DateTimeOffset.UtcNow);
        var expected = new RouteJourneyEstimator(complete, (_, _) => new(80, "category")).Estimate(route);
        Assert.True(expected.Ready, expected.Failure);
        Assert.Equal(expected, new RouteJourneyEstimator(actual, (_, _) => new(80, "category")).Estimate(route));
        MapScanCoverage Corridor(NativeMap map) => MapScanCoverage.Create(first.Node.Position, 1100, map, route, roads.Count * 18000);
        var corridor = Corridor(actual);
        Assert.Equal(Corridor(complete).RouteSegments, corridor.RouteSegments);
        Assert.InRange(corridor.RouteSegments.Sum(s => (double)Vector3.Distance(s.From, s.To)), 9999.9, 10000.1);
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
        var actual = NavigationBaseMap.Read(files, files.Path, TestContext.Current.CancellationToken);
        Assert.Same(actual.MapItems[road.Uid], actual.Nodes[unrelated.Node.Uid].ForwardItem);
        var error = Assert.Throws<InvalidDataException>(() => new GameMapData(actual));
        Assert.Contains(road.Uid.ToString("X16"), error.Message);
        Assert.Contains(unrelated.Node.Uid.ToString("X16"), error.Message);
    }

    [Fact]
    public void ACityMayReferenceAPhysicalNodeInAnotherSectorsSceneryFile()
    {
        using var files = new Fixture();
        var source = new NativeMap();
        var scenery = Model.Add(source, new(15, 2, 30), "scenery", default, default);
        for (int i = 1; i < 6; i++) CityArea.Add(source, new(i * 4000 + 15, 0, 10), "city", 60, 60);
        var city = CityArea.Add(source, new(24020, 1, 40), "city", 60, 60);
        source.Save(files.Directory, "test");

        // The stock writer normally puts a node alongside its owner. Make the far city refer to
        // the existing model's .aux node without changing that node's own declarations.
        string cityFile = files.SectorFile(6, "base");
        byte[] bytes = File.ReadAllBytes(cityFile);
        byte[] originalRef = BitConverter.GetBytes(city.Node.Uid);
        int index = bytes.AsSpan().IndexOf(originalRef);
        Assert.True(index >= 0 && bytes.AsSpan(index + originalRef.Length).IndexOf(originalRef) >= 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index), scenery.Node.Uid);
        File.WriteAllBytes(cityFile, bytes);

        var full = NativeMap.Open(files.Path, new DiskFileSystem());
        var actual = NavigationBaseMap.Read(files, files.Path, TestContext.Current.CancellationToken);
        var anchor = Assert.IsType<CityArea>(actual.MapItems[city.Uid]).Node;
        Assert.Equal(((CityArea)full.MapItems[city.Uid]).Node.Position, anchor.Position);
        Assert.Same(actual.Nodes[scenery.Node.Uid], anchor);
        Assert.Equal(scenery.Uid, anchor.ForwardItem?.Uid);
        Assert.False(actual.MapItems.ContainsKey(scenery.Uid));
        Assert.Equal(2, files.Opens[files.SectorFile(0, "aux")]);
        Assert.Equal(1, files.Opens[cityFile]);
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
