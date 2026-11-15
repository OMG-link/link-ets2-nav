using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class WideRoadProjectionTests
{
    [Fact]
    public void DeclaredHalfWidthAdmitsARecordedOuterLaneWithoutRelaxingHeadingOrHeight()
    {
        var road=new DirectedRoad("road",1,2,[Vector3.Zero,new(100,0,0)]){ReferenceHalfWidthMeters=13.5};
        var matcher=new DirectedPathMatcher();
        Assert.Equal(50,matcher.ProjectDistance(new(50,0,13.38f),Vector3.UnitX,[road]));
        Assert.Null(matcher.ProjectDistance(new(50,0,13.51f),Vector3.UnitX,[road]));
        Assert.Null(matcher.ProjectDistance(new(50,3.1f,13.38f),Vector3.UnitX,[road]));
        Assert.Null(matcher.ProjectDistance(new(50,0,13.38f),-Vector3.UnitX,[road]));
        Assert.Null(matcher.ProjectDistance(new(50,0,13.38f),Vector3.UnitX,[road with{ReferenceHalfWidthMeters=null}]));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)]
    public void InvalidWidthsNeverExtendTheDefaultTolerance(double width)
    {
        var road=new DirectedRoad("road",1,2,[Vector3.Zero,new(100,0,0)]){ReferenceHalfWidthMeters=width};
        Assert.Null(new DirectedPathMatcher().ProjectDistance(new(50,0,13),Vector3.UnitX,[road]));
    }

    [Fact]
    public void WidthIsCapturedInTheGraphAndPreparedSnapshotNeedsNoDefinitionReadsOnPositionTicks()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        int reads=0;var source=new RoadPathSource(map,roadHalfWidth:_=>{reads++;return 13.5;});
        var graph=source.GetNavigationGraph();Assert.Equal(1,reads);
        Assert.All(graph,e=>Assert.Equal(13.5,e.ReferenceHalfWidthMeters));
        var cached=new RoadPathSource(map,native:new NativeLaneGraph(graph));
        var route=RouteSnapshot.Parse([new(road.Node.Uid,100,0),new(road.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        foreach(float x in new[]{30,50,70})
            Assert.Single(cached.GetPath(new(x,0,13.38f),Vector3.UnitX,route,RouteAvailability.Available));
        Assert.Equal(1,reads);
    }

    [Fact]
    public void AParallelNonRouteRoadCannotBorrowAWideRoutesGeometry()
    {
        var map=new TruckLib.ScsMap.Map();var target=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        Road.Add(map,new(0,0,20),new(100,0,20),default);
        var source=new RoadPathSource(map,roadHalfWidth:r=>r.Uid==target.Uid?30:5);
        var route=RouteSnapshot.Parse([new(target.Node.Uid,100,0),new(target.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        Assert.Empty(source.GetPath(new(50,0,20),Vector3.UnitX,route,RouteAvailability.Available));
    }
}
