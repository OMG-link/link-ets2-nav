using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class NativeNodeConnectionsTests
{
    [Fact]
    public void PackedHostReferenceCountsKeepWholeUidGroupsAcrossBlocks()
    {
        const ulong maximum = ulong.MaxValue;
        var references = Enumerable.Range(0, 390).Select(i => (ulong)i * 3).ToList();
        references.AddRange(Enumerable.Repeat(381UL, 200));
        references.AddRange([maximum - 1, maximum]);
        references.Reverse();

        var counts = UnresolvedItemReferenceCounts.Build(references);

        Assert.Equal(1, counts.Count(0));
        Assert.Equal(201, counts.Count(381));
        Assert.Equal(0, counts.Count(382));
        Assert.Equal(1, counts.Count(384));
        Assert.Equal(0, counts.Count(maximum - 2));
        Assert.Equal(1, counts.Count(maximum - 1));
        Assert.Equal(1, counts.Count(maximum));
    }

    [Fact]
    public void OtherSideDistinguishesOpenEndFromMissingObjectAndLoadedConnection()
    {
        var firstWindow = new GameMap();
        var first = Road.Add(firstWindow, new(0, 0, 0), new(20, 0, 0), default);
        var second = first.Append(new(40, 0, 0));
        firstWindow.MapItems.Remove(second.Uid);
        firstWindow.Nodes.Remove(second.ForwardNode.Uid);

        var data = new GameMapData(firstWindow);
        Assert.Null(data.OtherConnectedItem((Node)first.Node, first).Issue);
        Assert.Null(data.OtherConnectedItem((Node)first.Node, first).Value);
        var missing = Assert.IsType<MissingMapObjects>(data.OtherConnectedItem((Node)first.ForwardNode, first).Issue);
        Assert.Equal(new MapObjectKey(MapObjectKind.Item, second.Uid), Assert.Single(missing.Keys));

        var secondWindow = new GameMap();
        secondWindow.MapItems.Add(second.Uid, second);
        secondWindow.Nodes.Add(second.ForwardNode.Uid, second.ForwardNode);
        data.Publish(secondWindow);
        Assert.Same(second, data.OtherConnectedItem((Node)first.ForwardNode, first).Value);
    }

    [Fact]
    public void RejectsAThirdComponentClaimingAnAlreadyOwnedInterfaceOnPublication()
    {
        var host = new GameMap();
        var first = Road.Add(host, new(0, 0, 0), new(20, 0, 0), default);
        var second = first.Append(new(40, 0, 0));
        var data = new GameMapData(host);
        var supplement = new GameMap();
        var third = Road.Add(supplement, new(0, 0, 10), new(20, 0, 10), default);
        third.ForwardNode = second.Node;

        var error = Assert.Throws<InvalidDataException>(() => data.Publish(supplement));
        Assert.Contains(second.Node.Uid.ToString("X16"), error.Message);
        Assert.Contains(third.Uid.ToString("X16"), error.Message);
        Assert.Null(data.GetItem(third.Uid));
    }

    [Fact]
    public void RejectsANodeThatClaimsAnUnrelatedRoad()
    {
        var map = new GameMap();
        var road = Road.Add(map, new(0, 0, 0), new(20, 0, 0), default);
        var unrelated = map.AddNode(new(100, 0, 100));
        unrelated.ForwardItem = road;

        var error = Assert.Throws<InvalidDataException>(() => new GameMapData(map));
        Assert.Contains(unrelated.Uid.ToString("X16"), error.Message);
        Assert.Contains(road.Uid.ToString("X16"), error.Message);
    }

    [Fact]
    public void ChecksAnEarlierNodeReferenceWhenTheTargetArrivesInADifferentWindow()
    {
        var firstWindow = new GameMap();
        var road = Road.Add(firstWindow, new(0, 0, 0), new(20, 0, 0), default);
        var secondWindow = new GameMap();
        var unrelated = Road.Add(secondWindow, new(0, 0, 20), new(20, 0, 20), default);
        var boundary = (Node)road.ForwardNode;
        boundary.ForwardItem = unrelated;
        var data = new GameMapData(firstWindow);

        Assert.IsType<MissingMapObjects>(data.OtherConnectedItem(boundary, road).Issue);
        var error = Assert.Throws<InvalidDataException>(() => data.Publish(secondWindow));
        Assert.Contains(boundary.Uid.ToString("X16"), error.Message);
        Assert.Contains(unrelated.Uid.ToString("X16"), error.Message);
        Assert.Null(data.GetItem(unrelated.Uid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChecksEarlierNodeEvenWhenNeitherSideWasInitiallyAvailable(bool fromHost)
    {
        var firstWindow = new GameMap();
        var boundary = firstWindow.AddNode(new(0, 0, 0));
        var secondWindow = new GameMap();
        var unrelated = Road.Add(secondWindow, new(0, 0, 20), new(20, 0, 20), default);
        var thirdWindow = new GameMap();
        var absent = Road.Add(thirdWindow, new(0, 0, 40), new(20, 0, 40), default);
        boundary.ForwardItem = unrelated;
        boundary.BackwardItem = absent;
        var data = new GameMapData(fromHost ? firstWindow : null);
        if (!fromHost)
            data.Publish(firstWindow);

        var error = Assert.Throws<InvalidDataException>(() => data.Publish(secondWindow));
        Assert.Contains(boundary.Uid.ToString("X16"), error.Message);
        Assert.Contains(unrelated.Uid.ToString("X16"), error.Message);
        Assert.Null(data.GetItem(unrelated.Uid));
    }

    [Fact]
    public void ChecksAnEarlierComponentClaimWhenTheUnresolvedNodeArrives()
    {
        var firstWindow = new GameMap();
        var road = Road.Add(firstWindow, new(0, 0, 0), new(20, 0, 0), default);
        firstWindow.Nodes.Remove(road.ForwardNode.Uid);
        var secondWindow = new GameMap();
        var node = new Node { Uid = road.ForwardNode.Uid, Position = road.ForwardNode.Position };
        secondWindow.Nodes.Add(node.Uid, node);
        var data = new GameMapData(firstWindow);

        var error = Assert.Throws<InvalidDataException>(() => data.Publish(secondWindow));
        Assert.Contains(node.Uid.ToString("X16"), error.Message);
        Assert.Contains(road.Uid.ToString("X16"), error.Message);
        Assert.Null(data.GetNode(node.Uid));
    }

#if DEBUG
    [Fact]
    public void OtherSideQueryRequiresItsComponentToBeAttached()
    {
        var map = new GameMap();
        var first = Road.Add(map, new(0, 0, 0), new(20, 0, 0), default);
        var other = Road.Add(map, new(0, 0, 20), new(20, 0, 20), default);
        var data = new GameMapData(map);

        Assert.Throws<NavigationContractException>(() => data.OtherConnectedItem((Node)first.Node, other));
    }
#endif
}
