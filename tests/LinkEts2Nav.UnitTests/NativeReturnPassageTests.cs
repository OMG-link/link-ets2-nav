using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NativeReturnPassageTests
{
    [Fact]
    public void OrdinaryGraphViewExcludesReturnsWithoutDiscardingRouteGeometry()
    {
        var straight=new DirectedRoad("prefab:1:0:1",10,20,[Vector3.Zero,Vector3.UnitX]);
        var returning=new DirectedRoad("prefab:1:0:0",10,10,[Vector3.Zero,Vector3.UnitX,Vector3.Zero]);
        var graph=new NativeLaneGraph([straight,returning]);
        Assert.Equal(2,graph.Edges.Count);Assert.Same(straight,Assert.Single(graph.ThroughTraffic.Edges));
        Assert.Same(graph.ThroughTraffic,graph.ThroughTraffic);Assert.Same(graph.ThroughTraffic,graph.ThroughTraffic.ThroughTraffic);
        var ordinary=new NativeLaneGraph([straight]);Assert.Same(ordinary,ordinary.ThroughTraffic);
        Assert.False((returning with{Id="roundabout:known"}).ReturnsToEntry);
    }
}
