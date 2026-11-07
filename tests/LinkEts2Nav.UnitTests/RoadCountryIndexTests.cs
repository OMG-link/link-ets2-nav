using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadCountryIndexTests
{
    private static readonly IReadOnlyDictionary<byte, string> Names =
        new Dictionary<byte, string> { [23] = "finland", [38] = "russia", [0] = "invalid-zero" };

    private static Node Node(ulong uid) => new() { Uid = uid };
    private static Road Road(GameMap map, ulong uid, INode start, INode end)
    {
        var road = new Road { Uid = uid, Node = start, ForwardNode = end };
        map.MapItems.Add(uid, road);
        return road;
    }
    private static void Join(Node node, IMapObject backward, IMapObject forward)
    {
        node.BackwardItem = backward;
        node.ForwardItem = forward;
    }
    private static void Border(Node node, byte backward, byte forward)
    {
        node.IsCountryBorder = true;
        node.BackwardCountry = backward;
        node.ForwardCountry = forward;
    }

    [Fact]
    public void BorderCountriesBelongToItemSidesRegardlessOfRoadTravelOrientation()
    {
        var map = new GameMap();
        var border = Node(2);
        // Deliberately reverse both roads' geometry relative to the reference sides.
        var first = Road(map, 10, border, Node(1));
        var second = Road(map, 20, Node(3), border);
        Join(border, second, first);
        border.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.7f);
        Border(border, 38, 23);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        Assert.Equal("finland", index.GetCountry(first.Uid));
        Assert.Equal("russia", index.GetCountry(second.Uid));
        index.AddSeed(first.Uid, "russia");
        Assert.Null(index.GetCountry(first.Uid));
        Assert.Equal("russia", index.GetCountry(second.Uid));
    }

    [Fact]
    public void PrefabPropagatesEvidenceAcrossItsOrdinaryRoadConnections()
    {
        var map = new GameMap();
        var a = Node(1);
        var b = Node(2);
        var left = Road(map, 10, Node(3), a);
        var right = Road(map, 20, b, Node(4));
        var prefab = new Prefab { Uid = 30, Nodes = [a, b] };
        map.MapItems.Add(prefab.Uid, prefab);
        Join(a, left, prefab);
        Join(b, prefab, right);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        Assert.Null(index.GetCountry(left.Uid));
        index.AddSeed(right.Uid, "finland");
        Assert.Equal("finland", index.GetCountry(left.Uid));
        Assert.Equal("finland", index.GetCountry(prefab.Uid));
    }

    [Fact]
    public void OrdinaryNodeCountryBytesAreIgnoredAndAnExplicitCitySeedCanResolveItsComponent()
    {
        var map = new GameMap();
        var join = Node(1);
        var left = Road(map, 10, Node(2), join);
        var right = Road(map, 20, join, Node(3));
        Join(join, left, right);
        join.BackwardCountry = 38;
        join.ForwardCountry = 23;
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        Assert.Null(index.GetCountry(left.Uid));
        index.AddSeed(left.Uid, "finland");
        index.AddSeed(right.Uid, "finland");
        Assert.Equal("finland", index.GetCountry(right.Uid));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)200)]
    public void UnknownBorderCountryPermanentlyPreventsLaterSeedsFromGuessing(byte unknown)
    {
        var map = new GameMap();
        var border = Node(1);
        var first = Road(map, 10, Node(2), border);
        var second = Road(map, 20, border, Node(3));
        Join(border, first, second);
        Border(border, unknown, 23);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        index.AddSeed(first.Uid, "finland");
        Assert.Null(index.GetCountry(first.Uid));
        Assert.True(index.IsUncertain(first.Uid));
        Assert.False(index.IsUncertain(second.Uid));
        Assert.Equal("finland", index.GetCountry(second.Uid));
    }

    [Fact]
    public void ConflictingBorderSeedsRemainUnknownEvenAfterMatchingCitySeed()
    {
        var map = new GameMap();
        var a = Node(1);
        var b = Node(2);
        var road = Road(map, 10, a, b);
        a.ForwardItem = road;
        b.BackwardItem = road;
        Border(a, 0, 23);
        Border(b, 38, 0);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        index.AddSeed(road.Uid, "finland");
        Assert.Null(index.GetCountry(road.Uid));
    }

    [Fact]
    public void ConflictingCitySeedsCannotBeOverwritten()
    {
        var map = new GameMap();
        var road = Road(map, 10, Node(1), Node(2));
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        index.AddSeed(road.Uid, "finland");
        index.AddSeed(road.Uid, "russia");
        index.AddSeed(road.Uid, "finland");
        Assert.Null(index.GetCountry(road.Uid));
    }

    [Fact]
    public void MissingItemsNeverConnectComponentsOrAppearInTheIndex()
    {
        var map = new GameMap();
        var a = Node(1);
        var b = Node(2);
        var first = Road(map, 10, Node(3), a);
        var missing = new UidReference { Uid = 999 };
        var second = Road(map, 20, b, Node(4));
        Join(a, first, missing);
        Join(b, missing, second);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        index.AddSeed(first.Uid, "finland");
        index.AddSeed(999, "russia");
        Assert.True(index.Contains(first.Uid));
        Assert.False(index.Contains(999));
        Assert.Null(index.GetCountry(999));
        Assert.Null(index.GetCountry(second.Uid));
    }

    [Fact]
    public void AUidReferenceCanResolveOnlyToAnItemActuallyPresentInTheMap()
    {
        var map = new GameMap();
        var join = Node(1);
        var first = Road(map, 10, Node(2), join);
        var second = Road(map, 20, join, Node(3));
        Join(join, new UidReference { Uid = first.Uid }, new UidReference { Uid = second.Uid });
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        index.AddSeed(first.Uid, "finland");
        Assert.Equal("finland", index.GetCountry(second.Uid));
    }

    [Fact]
    public void UnreferencedGlobalNodesAreNotTraversedAndMapChangesDoNotMutateTheIndex()
    {
        var map = new GameMap();
        var road = Road(map, 10, Node(1), Node(2));
        var unrelated = Node(3);
        unrelated.ForwardItem = road;
        Border(unrelated, 0, 38);
        map.Nodes.Add(unrelated.Uid, unrelated);
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        Assert.Null(index.GetCountry(road.Uid));
        index.AddSeed(road.Uid, "finland");
        map.MapItems.Clear();
        road.Node = Node(4);
        Assert.True(index.Contains(10));
        Assert.Equal("finland", index.GetCountry(10));
    }

    [Fact]
    public void CancelledBuildDoesNotReturnAnIndex()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoadCountryIndex.Build(new GameMap(), Names, stop.Token));
    }

    [Fact]
    public void UncertaintyDistinguishesMissingEvidenceFromConflictingEvidence()
    {
        var map = new GameMap();
        var first = Road(map, 10, Node(1), Node(2));
        var second = Road(map, 20, Node(3), Node(4));
        var index = RoadCountryIndex.Build(map, Names, TestContext.Current.CancellationToken);
        Assert.Null(index.GetCountry(first.Uid));
        Assert.False(index.IsUncertain(first.Uid));
        Assert.False(index.IsUncertain(999));
        index.AddSeed(first.Uid, "finland");
        Assert.False(index.IsUncertain(first.Uid));
        index.AddSeed(first.Uid, "russia");
        Assert.True(index.IsUncertain(first.Uid));
        index.AddSeed(first.Uid, "finland");
        Assert.True(index.IsUncertain(first.Uid));
        Assert.Null(index.GetCountry(first.Uid));
        Assert.Null(index.GetCountry(second.Uid));
        Assert.False(index.IsUncertain(second.Uid));
    }
    private sealed class UidReference : IMapObject
    {
        public ulong Uid { get; set; }
    }
}
