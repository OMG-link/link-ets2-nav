using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class JunctionNavigationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    private static ControlNode Control(Vector3 position, bool input, int curve) => new()
    {
        Position = position, Direction = input ? Vector3.UnitZ : -Vector3.UnitZ,
        InputLines = Enumerable.Range(0, 8).Select(i => input && i == 0 ? curve : -1).ToArray(),
        OutputLines = Enumerable.Range(0, 8).Select(i => !input && i == 0 ? curve : -1).ToArray()
    };

    [Theory]
    [InlineData(18)]
    [InlineData(20)]
    [InlineData(24)]
    public void ShortPrefabDoesNotLoseOwnershipBetweenNearbyRoadEndpoints(int gap)
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map, new(0, 0, 0), new(0, 0, 200), default);
        var outgoing = Road.Add(map, new(0, 0, 200 + gap), new(0, 0, 800), default);
        DirectedRoad[] links = [new("prefab:1:0:1", incoming.ForwardNode.Uid, outgoing.Node.Uid,
            [new(0, 0, 200), new(0, 0, 200 + gap)])];
        var source = new RoadPathSource(map, links);
        var route = RouteSnapshot.Parse([new(incoming.Node.Uid, 800, 40), new(incoming.ForwardNode.Uid, 600, 30),
            new(outgoing.Node.Uid, 600 - gap, 29), new(outgoing.ForwardNode.Uid, 0, 0)], Now);
        var camera = new MonitoredCamera("camera", new(0, 0, 700), Vector3.UnitZ);
        foreach (int z in Enumerable.Range(190, gap + 21))
        {
            var position = new Vector3(3.5f, 0, z);
            var match = source.MatchCamera(position, Vector3.UnitZ, camera, route, RouteAvailability.Available);
            Assert.Equal(CameraApproach.RouteAhead, match.Approach);
            Assert.InRange(match.RoadDistanceMeters!.Value, 700 - z - .01, 700 - z + .01);
            var without = source.MatchCamera(position, Vector3.UnitZ, camera,
                RouteSnapshot.Parse([], Now), RouteAvailability.None);
            Assert.Equal(CameraApproach.UnavoidableAhead, without.Approach);
        }
    }
    [Fact]
    public void NearbyPrefabCannotBorrowVehicleFromAnActualUnrelatedRoad()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map, new(0, 0, 0), new(0, 0, 200), default);
        var outgoing = Road.Add(map, new(0, 0, 220), new(0, 0, 800), default);
        Road.Add(map, new(8, 0, 200), new(8, 0, 220), default);
        DirectedRoad[] links = [new("prefab:1:0:1", incoming.ForwardNode.Uid, outgoing.Node.Uid,
            [new(0, 0, 200), new(0, 0, 220)])];
        var source = new RoadPathSource(map, links);
        var route = RouteSnapshot.Parse([new(incoming.Node.Uid, 800, 40), new(incoming.ForwardNode.Uid, 600, 30),
            new(outgoing.Node.Uid, 580, 29), new(outgoing.ForwardNode.Uid, 0, 0)], Now);
        var match = source.MatchCamera(new(8, 0, 210), Vector3.UnitZ,
            new("camera", new(0, 0, 700), Vector3.UnitZ), route, RouteAvailability.Available);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }
    [Fact]
    public void ActualPpdNavigationCurveBridgesRouteAndMeasuresAcrossJunction()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map, new(0, 0, 0), new(0, 0, 200), default);
        var outgoing = Road.Add(map, new(0, 0, 240), new(0, 0, 800), default);
        var prefab = new Prefab { Nodes = [incoming.ForwardNode, outgoing.Node] };
        map.MapItems.Add(prefab.Uid, prefab);
        var descriptor = new PrefabDescriptor
        {
            Nodes = [Control(new(0, 0, 200), true, 0), Control(new(0, 0, 240), false, 0)],
            NavCurves = [new NavCurve { StartPosition = new(0, 0, 200), EndPosition = new(0, 0, 240),
                StartRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                EndRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Length = 40 }]
        };
        var links = PrefabPathGeometry.Build(prefab, descriptor);
        var link = Assert.Single(links);
        Assert.Equal(incoming.ForwardNode.Uid, link.StartNodeUid);
        Assert.Equal(outgoing.Node.Uid, link.EndNodeUid);
        var source = new RoadPathSource(map, links);
        var route = RouteSnapshot.Parse([new(incoming.Node.Uid, 800, 40), new(incoming.ForwardNode.Uid, 600, 30),
            new(outgoing.Node.Uid, 560, 28), new(outgoing.ForwardNode.Uid, 0, 0)], Now);
        var camera = new MonitoredCamera("camera", new(0, 0, 700), Vector3.UnitZ);
        var before = source.MatchCamera(new(0, 0, 100), Vector3.UnitZ, camera, route, RouteAvailability.Available);
        Assert.Equal(CameraApproach.RouteAhead, before.Approach);
        Assert.Equal(600, before.RoadDistanceMeters!.Value, 2);
        var inside = source.MatchCamera(new(0, 0, 220), Vector3.UnitZ, camera, route, RouteAvailability.Available);
        Assert.Equal(CameraApproach.RouteAhead, inside.Approach);
        Assert.Equal(480, inside.RoadDistanceMeters!.Value, 2);
        var insideWithoutRoute = source.MatchCamera(new(0,0,220), Vector3.UnitZ, camera,
            RouteSnapshot.Parse([], Now), RouteAvailability.None);
        Assert.Equal(CameraApproach.UnavoidableAhead, insideWithoutRoute.Approach);
        Assert.Equal(480, insideWithoutRoute.RoadDistanceMeters!.Value, 2);
        var noRoute = source.MatchCamera(new(0, 0, 100), Vector3.UnitZ, camera, RouteSnapshot.Parse([], Now), RouteAvailability.None);
        Assert.Equal(CameraApproach.UnavoidableAhead, noRoute.Approach);
        Assert.Equal(600, noRoute.RoadDistanceMeters!.Value, 2);
        var internalNodes = RouteSnapshot.Parse([new(incoming.Node.Uid, 800, 40), new(incoming.ForwardNode.Uid, 600, 30),
            new(123456, 580, 29), new(outgoing.Node.Uid, 560, 28), new(outgoing.ForwardNode.Uid, 0, 0)], Now);
        Assert.Equal(480, source.MatchCamera(new(0, 0, 220), Vector3.UnitZ, camera, internalNodes,
            RouteAvailability.Available).RoadDistanceMeters!.Value, 2);
    }

    [Fact]
    public void NoRouteCannotChooseBetweenTwoLegalJunctionExits()
    {
        var map = new TruckLib.ScsMap.Map();
        var incoming = Road.Add(map, new(0, 0, 0), new(0, 0, 200), default);
        var straight = Road.Add(map, new(0, 0, 240), new(0, 0, 800), default);
        var turn = Road.Add(map, new(40, 0, 200), new(500, 0, 200), default);
        DirectedRoad[] links = [
            new("prefab:1:0:1", incoming.ForwardNode.Uid, straight.Node.Uid, [new(0,0,200),new(0,0,240)]),
            new("prefab:1:0:2", incoming.ForwardNode.Uid, turn.Node.Uid, [new(0,0,200),new(40,0,200)])];
        var source = new RoadPathSource(map, links);
        var match = source.MatchCamera(new(0,0,100), Vector3.UnitZ,
            new("camera",new(0,0,700),Vector3.UnitZ),RouteSnapshot.Parse([], Now),RouteAvailability.None);
        Assert.Equal(CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void SustainedZeroNavigationDiscardsRepeatedOldRouteButNotMissingOrTornData()
    {
        var store = new GameSnapshotStore();
        var oldRoute = RouteSnapshot.Parse([new(7, 1000, 30)], Now);
        for (int i=0;i<3;i++)
        {
            var time = Now.AddMilliseconds(i*600);
            store.UpdateVehicle(new(0,0,0,0,10,0,true,false,(ulong)(100+i),time));
            store.UpdateRoute(oldRoute with { ReceivedAt = time });
            Assert.Equal(i<2 ? RouteAvailability.Invalid : RouteAvailability.None, store.Capture(time).RouteAvailability);
        }
        var last = Now.AddMilliseconds(1200);
        store.UpdateRoute(RouteSnapshot.Parse([new(7, -1, 30)], last));
        Assert.Equal(RouteAvailability.Invalid, store.Capture(last).RouteAvailability);
        Assert.Equal(RouteAvailability.Stale, store.Capture(last.AddSeconds(4)).RouteAvailability);
    }

    [Fact]
    public void RearmNeedsNearPassageThenFiftyMetersSeparation()
    {
        var engine = new CameraWarningEngine();
        WarningSnapshot Data(double distance, CameraApproach approach=CameraApproach.RouteAhead) =>
            new("s","r",true,true,Now,[new("camera",approach,distance)]);
        Assert.Single(engine.Update(Data(490),Now));
        Assert.Empty(engine.Update(Data(900),Now));
        Assert.Empty(engine.Update(Data(490),Now));
        Assert.Single(engine.Update(Data(199),Now));
        Assert.Empty(engine.Update(Data(10,CameraApproach.Behind),Now));
        Assert.Empty(engine.Update(Data(49,CameraApproach.Behind),Now));
        Assert.Empty(engine.Update(Data(30),Now));
        Assert.Empty(engine.Update(Data(50,CameraApproach.Behind),Now));
        Assert.Empty(engine.Update(Data(600,CameraApproach.Behind),Now));
        Assert.Single(engine.Update(Data(490),Now));
    }

    [Fact]
    public void RotatedPrefabWithChangedOriginKeepsCorrectWorldConnections()
    {
        var map = new TruckLib.ScsMap.Map();
        var a = map.AddNode(new(10,5,30), true);
        var b = map.AddNode(new(10,5,130), false);
        var c = map.AddNode(new(-90,5,30), false);
        var prefab = new Prefab { Nodes = [a,b,c] };
        prefab.ChangeOrigin(2);
        var descriptor = new PrefabDescriptor
        {
            Nodes = [Control(Vector3.Zero,true,0), Control(new(100,0,0),false,0), Control(new(0,0,100),false,-1)],
            NavCurves = [new NavCurve { StartPosition=Vector3.Zero,EndPosition=new(100,0,0),Length=100,
                StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2),
                EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2) }]
        };
        var link = Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.Equal(a.Uid,link.StartNodeUid);
        Assert.Equal(b.Uid,link.EndNodeUid);
        Assert.Equal(50,new DirectedPathMatcher().ProjectDistance(new(10,5,80),Vector3.UnitZ,[link])!.Value,2);
        b.Position += new Vector3(4,0,0);
        Assert.Empty(PrefabPathGeometry.Build(prefab,descriptor));
    }

    [Theory]
    [InlineData(-2.496f)]
    [InlineData(.519f)]
    public void GradedPrefabConnectsEndpointsAndProjectsAlongSlope(float grade)
    {
        var map = new TruckLib.ScsMap.Map();
        var a = map.AddNode(new(0, 10, 0), true);
        var b = map.AddNode(new(0, 10 + grade, 40), false);
        var descriptor = new PrefabDescriptor
        {
            Nodes = [Control(Vector3.Zero, true, 0), Control(new(0, 0, 40), false, 0)],
            NavCurves = [new NavCurve { StartPosition = Vector3.Zero, EndPosition = new(0, 0, 40),
                StartRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                EndRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Length = 40 }]
        };
        var link = Assert.Single(PrefabPathGeometry.Build(new Prefab { Nodes = [a, b] }, descriptor));
        Assert.Equal(a.Position, link.Points[1]);
        Assert.Equal(b.Position.Y, link.Points[^2].Y, 4);
        foreach (var point in link.Points)
            Assert.Equal(10 + grade * point.Z / 40, point.Y, 4);
        Assert.InRange(new DirectedPathMatcher().ProjectDistance(new(0, 10 + grade / 2, 20),
            Vector3.UnitZ, [link])!.Value, 19.9, 20.2);
        // Changing a two-node bearing can be a valid rotation; stretching cannot.
        b.Position += new Vector3(0, 0, 4);
        Assert.Empty(PrefabPathGeometry.Build(new Prefab { Nodes = [a, b] }, descriptor));
    }

    [Fact]
    public void GradingRetainsTheNativeCrestAcrossAChainOfCurves()
    {
        var map = new TruckLib.ScsMap.Map();
        var a = map.AddNode(new(0, 10, 0), true);
        var b = map.AddNode(new(0, 14, 40), false);
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        var descriptor = new PrefabDescriptor
        {
            Nodes = [Control(Vector3.Zero,true,0),Control(new(0,0,40),false,1)],
            NavCurves = [
                new NavCurve { StartPosition=Vector3.Zero,EndPosition=new(0,2,20),Length=20.1f,
                    StartRotation=forward,EndRotation=forward },
                new NavCurve { StartPosition=new(0,2,20),EndPosition=new(0,0,40),Length=20.1f,
                    StartRotation=forward,EndRotation=forward }]
        };
        descriptor.NavCurves[0].NextLines.Add(1);
        var link = Assert.Single(PrefabPathGeometry.Build(new Prefab { Nodes=[a,b] },descriptor));
        var crest = link.Points.MinBy(p => Math.Abs(p.Z-20));
        // Base 10 + native crest 2 + half of the four-metre endpoint grade.
        Assert.Equal(14,crest.Y,3);
        Assert.Equal(10,link.Points[1].Y,3);
        Assert.Equal(14,link.Points[^2].Y,3);
    }

    [Fact]
    public void FrozenSimulationCannotConfirmThatNavigationWasCleared()
    {
        var store = new GameSnapshotStore();
        for (int i=0;i<3;i++)
        {
            var time = Now.AddMilliseconds(i*600);
            store.UpdateVehicle(new(0,0,0,0,10,0,true,false,100,time));
            store.UpdateRoute(RouteSnapshot.Parse([new(7,1000,30)],time));
        }
        Assert.Equal(RouteAvailability.Invalid,store.Capture(Now.AddMilliseconds(1200)).RouteAvailability);
    }

}
