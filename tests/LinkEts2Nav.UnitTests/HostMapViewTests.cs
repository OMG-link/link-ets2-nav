using LinkEts2Nav.Map;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;
namespace LinkEts2Nav.UnitTests;
public sealed class HostMapViewTests
{
    [Fact]
    public void SharesHostRoadAcrossSectorBoundaryWithoutChangingHostOrSupplement()
    {
        var host = new GameMap();
        var road = Road.Add(host, new(3990,0,0), new(4010,0,0), default);
        var originalParent = road.Node.Parent;
        var supplement = new GameMap();
        var missing = Road.Add(supplement, new(4100,0,0), new(4200,0,0), default);
        new HostMapView(host).MergeInto(supplement, [new(1,0)]);
        Assert.Same(road, supplement.MapItems[road.Uid]);
        Assert.Same(road.Node, supplement.Nodes[road.Node.Uid]);
        Assert.Same(missing, supplement.MapItems[missing.Uid]);
        Assert.Same(originalParent, road.Node.Parent);
        Assert.Single(host.MapItems);
        Assert.DoesNotContain(missing.Uid, host.MapItems.Keys);
    }
}
