using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
using NativeMap = TruckLib.ScsMap.Map;
using PrefabDescriptor = TruckLib.Models.Ppd.PrefabDescriptor;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationSectorIndexTests
{
    [Fact]
    public void UsesTheSerializedSectorForAllPhysicalTablesAndRetainsBothPortDeclarations()
    {
        using var files = new Fixture();
        var map = new NativeMap();
        // The segment centre and both physical rows are written in sector zero. Its second point
        // lies in sector one, so using the endpoint's coordinates would select the wrong file.
        var road = Road.Add(map, new(3700, 0, 30), new(4050, 0, 30), "local");
        var scenery = Model.Add(map, new(-4100, 1, 30), "model", default, default);
        var sound = Sound.Add(map, new(8010, 1, 20), "sound");
        var dock = AddDock(map, 12010);
        var reverseDock = AddDock(map, -100);
        var ferry = Ferry.Add(map, new(100, 1, 20), "port");
        ferry.Prefab = dock;
        reverseDock.Ferry = ferry;
        var unlinked = Ferry.Add(map, new(110, 1, 20), "other");
        unlinked.Prefab = new Prefab { Uid = 0 }; // Native Ferry records encode an absent prefab as UID zero.
        map.Save(files.Root, "map");
        using (var index = files.Open())
        {
            CheckNode(road.ForwardNode, new(0, 0));
            CheckNode(scenery.Node, new(-2, 0));
            CheckNode(sound.Node, new(2, 0));
            Assert.True(index.TryGetItem(road.Uid, out var roadSector));
            Assert.Equal(new(0, 0), roadSector);
            Assert.True(index.TryGetItem(dock.Uid, out var dockSector));
            Assert.Equal(new(3, 0), dockSector);
            Assert.True(index.TryGetItem(ferry.Uid, out _));
            Assert.False(index.TryGetItem(scenery.Uid, out _));
            Assert.False(index.TryGetItem(sound.Uid, out _));
            Assert.False(index.TryGetNode(0xF123456789ABCDEF, out _));
            Assert.Equal(new[]{ferry.Uid, dock.Uid, reverseDock.Uid, unlinked.Uid}.Order(), index.FerryComponents);

            void CheckNode(INode native, SectorCoordinate expected)
            {
                Assert.True(index.TryGetNode(native.Uid, out var location));
                Assert.Equal(new(expected, native.BackwardItem?.Uid ?? 0, native.ForwardItem?.Uid ?? 0), location);
            }
        }
        int sourceReads = files.Opens;
        using (var warm = files.Open())
        {
            Assert.True(warm.TryGetNode(sound.Node.Uid, out _));
            Assert.Equal(sourceReads, files.Opens);
        }
        using (var corrupt = File.Open(files.Index, FileMode.Open, FileAccess.ReadWrite))
        {
            corrupt.Position = corrupt.Length - 33; // Keep header and length intact.
            int value = corrupt.ReadByte();
            corrupt.Position--;
            corrupt.WriteByte((byte)(value ^ 1));
        }
        using var repaired = files.Open();
        Assert.True(files.Opens > sourceReads);
        Assert.True(repaired.TryGetNode(road.ForwardNode.Uid, out _));
    }

    [Fact]
    public void RejectsAThirdPartyNodeClaimEvenWhenItLivesOnlyInAnotherSectorsAuxFile()
    {
        using var files = new Fixture();
        var map = new NativeMap();
        var road = Road.Add(map, new(200, 1, 30), new(300, 1, 30), "local");
        var scenery = Model.Add(map, new(17000, 1, 30), "model", default, default);
        scenery.Node.ForwardItem = road;
        map.Save(files.Root, "map");
        var error = Assert.Throws<InvalidDataException>(() => files.Open());
        Assert.Contains(road.Uid.ToString("X16"), error.Message);
        Assert.Contains(scenery.Node.Uid.ToString("X16"), error.Message);
        Assert.False(File.Exists(files.Index));
    }

    [Fact]
    public void CancelledBuildDoesNotPublishAnyPartialDirectoryOrIndex()
    {
        using var files = new Fixture();
        var map = new NativeMap();
        Road.Add(map, new(10, 0, 20), new(100, 0, 20), "local");
        map.Save(files.Root, "map");
        using var cancel = new CancellationTokenSource();
        files.AfterOpen = cancel.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => files.Open(cancel.Token));
        Assert.False(File.Exists(files.Index));
        Assert.Empty(Directory.GetDirectories(files.Root, "*.tmp"));
        files.AfterOpen = null;
        using var rebuilt = files.Open();
        Assert.True(rebuilt.TryGetItem(map.MapItems.Keys.Single(), out _));
    }

    private static Prefab AddDock(NativeMap map, int x) => Prefab.Add(map, new(x, 0, 20), "dock",
        new PrefabDescriptor { Nodes = [new() { Position = Vector3.Zero, Direction = -Vector3.UnitZ }] });

    private sealed class Fixture : IFileSystem, IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("nav-sector-index-").FullName;
        internal string Index => Path.Combine(Root, "index.bin");
        internal NavigationSectorIndex Open(CancellationToken? token = null) =>
            NavigationSectorIndex.OpenOrBuild(this, Path.Combine(Root, "map.mbd"), Index, token ?? TestContext.Current.CancellationToken);
        internal int Opens { get; private set; }
        internal Action? AfterOpen { get; set; }
        public char DirectorySeparator => Path.DirectorySeparatorChar;
        public IList<string> GetFiles(string path) => Directory.GetFiles(path).Order(StringComparer.Ordinal).ToArray();
        public bool FileExists(string path) => File.Exists(path);
        public string GetParent(string path) => Path.GetDirectoryName(path)!;
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);
        public Stream Open(string path) { Opens++; AfterOpen?.Invoke(); return File.OpenRead(path); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
