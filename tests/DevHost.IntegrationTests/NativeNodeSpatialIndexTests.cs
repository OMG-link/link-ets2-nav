using System.Reflection;
using TruckLib.ScsMap;
using TruckLib.ScsMap.Collections;

namespace DevHost.IntegrationTests;

[CollectionDefinition(nameof(NativeNodeSpatialIndexTests), DisableParallelization = true)]
public sealed class NodeIndexModeCollection { }

[Collection(nameof(NativeNodeSpatialIndexTests))]
public sealed class NativeNodeSpatialIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMapFileAndUidLookupsKeepTheirNodesAndOwnership(bool navigationOnly)
    {
        string directory = Path.Combine(Path.GetTempPath(), "nav-node-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = InMode(navigationOnly, () => new Map());
            var road = Road.Add(source, new(20, 3, 10), new(70, 7, 20), default);
            source.Save(directory, "fixture");
            Assert.Equal(!navigationOnly, HasIndex(source.Nodes));
            var loaded = InMode(navigationOnly, () => Map.Open(Path.Combine(directory, "fixture.mbd")));
            var read = Assert.IsType<Road>(loaded.MapItems[road.Uid]);
            Assert.Same(loaded, read.Node.Parent);
            Assert.Same(read.Node, loaded.Nodes[road.Node.Uid]);
            Assert.Same(read, read.Node.ForwardItem);
            Assert.Equal(!navigationOnly, HasIndex(loaded.Nodes));
            Assert.Same(read.Node, Assert.Single(loaded.Nodes.Within(19, 9, 21, 11)));
            Assert.True(HasIndex(loaded.Nodes));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialQueriesUseTheCurrentNodesAcrossEditsAndConcurrentFirstReaders(bool navigationOnly)
    {
        var map = InMode(navigationOnly, () => new Map());
        var nodes = Enumerable.Range(0, 128).Select(i => map.AddNode(new(i * 50, 0, i * 50))).ToArray();
        var moved = nodes[10];
        moved.Move(new(-400, 5, -400));
        var replacement = new Node { Uid = nodes[11].Uid, Position = new(-700, 5, -700), Parent = map };
        map.Nodes[replacement.Uid] = replacement;
        Assert.True(map.Nodes.Remove(nodes[12].Uid));
        nodes[12].Move(new(-1300, 5, -1300));
        Assert.Equal(!navigationOnly, HasIndex(map.Nodes));

        Parallel.For(0, 8, _ => Assert.Same(moved, Assert.Single(map.Nodes.Within(-401, -401, -399, -399))));
        Assert.True(HasIndex(map.Nodes));
        Assert.Empty(map.Nodes.Within(499, 499, 601, 601));
        Assert.Empty(map.Nodes.Within(-1301, -1301, -1299, -1299));
        Assert.Same(replacement, Assert.Single(map.Nodes.Within(-701, -701, -699, -699)));
        moved.Move(new(8200, 5, 8200));
        Assert.Empty(map.Nodes.Within(-401, -401, -399, -399));
        Assert.Same(moved, Assert.Single(map.Nodes.Within(8199, 8199, 8201, 8201)));
        var extra = new Node { Position = new(-1550, 0, -1550), Parent = map };
        var entry = KeyValuePair.Create(extra.Uid, (INode)extra);
        map.Nodes.Add(entry);
        Assert.Throws<ArgumentException>(() => map.Nodes.Add(entry));
        Assert.Same(extra, Assert.Single(map.Nodes.Within(-1551, -1551, -1549, -1549)));
        Assert.True(map.Nodes.Remove(entry));
        Assert.Equal(map.Nodes.Count, map.Nodes.Within(-10000, -10000, 10000, 10000).Count);
        map.Nodes.Clear();
        Assert.Empty(map.Nodes.Within(-10000, -10000, 10000, 10000));
        var empty = InMode(navigationOnly, () => new NodeDictionary());
        Assert.Empty(empty.Within(0, 0, 0, 0));
    }

    private static bool HasIndex(NodeDictionary nodes) => typeof(NodeDictionary)
        .GetProperty("Tree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nodes) is not null;

    private static T InMode<T>(bool enabled, Func<T> create)
    {
        const string setting = "LinkEts2Nav.NavigationOnly";
        AppContext.TryGetSwitch(setting, out bool previous);
        AppContext.SetSwitch(setting, enabled);
        try { return create(); }
        finally { AppContext.SetSwitch(setting, previous); }
    }
}
