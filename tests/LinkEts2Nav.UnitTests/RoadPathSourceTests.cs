using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadPathSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
    private static Vector3 Point(float x, float z = 0, float y = 0) => new(x, y, z);
    private static Road AddRoad(GameMap map, Vector3 start, Vector3 end) =>
        Road.Add(map, start, end, default);
    private static RouteSnapshot Route(params (ulong Uid, float Distance)[] nodes) =>
        RouteSnapshot.Parse(nodes.Select(node => new RouteNode(node.Uid, node.Distance, node.Distance / 20)), Now);

    private static DirectedRoad Passage(ulong owner, ulong entry, ulong exit, float z, params float[] laneOffsets)
    {
        var reference = new[] { Point(0, z), Point(50, z) };
        return new($"prefab:{owner:X16}:{entry}:{exit}", entry, exit, reference)
        {
            LanePaths = laneOffsets.Select((offset, i) => new PrefabLanePath(owner, entry, exit,
                i, i, i, i, laneOffsets.Length, laneOffsets.Length, [i],
                [Point(0, offset), Point(50, offset)], 50)).ToArray()
        };
    }

    private static (GameMap Map,Road[] Roads,RouteSnapshot Route) LongLoadedRoute()
    {
        var map=new GameMap();var roads=new List<Road>{AddRoad(map,Point(0),Point(1000))};
        for(int i=2;i<=10;i++)roads.Add(roads[^1].Append(Point(i*1000)));
        var nodes=new[]{(roads[0].Node.Uid,10000f)}.Concat(roads.Select((r,i)=>(r.ForwardNode.Uid,(float)(9000-i*1000)))).ToArray();
        return(map,roads.ToArray(),Route(nodes));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(499)]
    [InlineData(501)]
    public void KnownSdkRoadUidsKeepEntireLoadedRouteBeyondLocalizationRadius(float x)
    {
        var s=LongLoadedRoute();var source=new RoadPathSource(s.Map);
        var path=source.GetPath(Point(x),Vector3.UnitX,s.Route,s.Route.Availability);
        Assert.Equal(s.Roads.Select(r=>r.Uid.ToString("X16")),path.Select(p=>p.Id));
        Assert.Equal(10000,path.Sum(p=>p.Points.Zip(p.Points.Skip(1)).Sum(q=>(double)Vector3.Distance(q.First,q.Second))),2);
    }

    [Fact]
    public void Crossing2500MeterEndpointBoundaryDoesNotAttachANewRouteTail()
    {
        var s=LongLoadedRoute();var source=new RoadPathSource(s.Map);
        var before=source.GetPath(Point(499),Vector3.UnitX,s.Route,s.Route.Availability);
        var after=source.GetPath(Point(501),Vector3.UnitX,s.Route,s.Route.Availability);
        Assert.Equal(before.Select(p=>p.Id),after.Select(p=>p.Id));
        Assert.Equal(10,before.Count);
    }

    [Theory]
    [InlineData(799,true)]
    [InlineData(800,true)]
    [InlineData(800.01f,false)]
    [InlineData(1000,false)]
    [InlineData(1199.99f,false)]
    [InlineData(1200,true)]
    [InlineData(1201,true)]
    public void LocalizationIncludesRoadWhenEitherEndpointIsWithinEightHundredMeters(float x,bool near)
    {
        // A deliberately long test road separates the endpoint coarse filter
        // from the player's exact interior projection onto its centerline.
        var map=new GameMap();var road=AddRoad(map,Point(0),Point(2000));
        var source=new RoadPathSource(map);var path=source.GetNavigationGraph()
            .Where(e=>e.StartNodeUid==road.Node.Uid).ToArray();
        Assert.Equal(near,source.IsPositionOnPath(Point(x),path));
        var noRoute=Route();
        Assert.Equal(near,source.GetPath(Point(x),Vector3.UnitX,noRoute,noRoute.Availability).Count==1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousDistantUidStillBreaksTheContinuousRoute(bool ambiguous)
    {
        var s=LongLoadedRoute();var route=s.Route;
        if(ambiguous)
        {
            var duplicate=AddRoad(s.Map,Point(7000,100),Point(8000,100));
            duplicate.Node=s.Roads[7].Node;duplicate.ForwardNode=s.Roads[7].ForwardNode;
        }
        else route=Route(s.Route.Nodes.Select((n,i)=>(i==8?999UL:n.Uid,n.DistanceToEndMeters)).ToArray());
        var path=new RoadPathSource(s.Map).GetPath(Point(100),Vector3.UnitX,route,route.Availability);
        Assert.NotEmpty(path);
        Assert.DoesNotContain(path,p=>p.Id==s.Roads[7].Uid.ToString("X16"));
        Assert.DoesNotContain(path,p=>p.Id==s.Roads[^1].Uid.ToString("X16"));
    }

    [Fact]
    public void LoadedConflictingConnectionIsLoggedOnceButMissingMapTailIsNotAnError()
    {
        var s=LongLoadedRoute();
        var duplicate=AddRoad(s.Map,Point(7000,100),Point(8000,100));
        duplicate.Node=s.Roads[7].Node;duplicate.ForwardNode=s.Roads[7].ForwardNode;
        var lines=new List<string>();var source=new RoadPathSource(s.Map,log:lines.Add);
        source.GetPath(Point(100),Vector3.UnitX,s.Route,s.Route.Availability);
        source.GetPath(Point(150),Vector3.UnitX,s.Route,s.Route.Availability);
        Assert.Single(lines,l=>l.StartsWith("导航连接解析错误："));
        Assert.Contains(s.Roads[7].Node.Uid.ToString("X16"),lines[0]);
        lines.Clear();var missing=new RoadPathSource(new GameMap(),log:lines.Add);
        missing.GetPath(Point(100),Vector3.UnitX,s.Route,s.Route.Availability);
        Assert.Empty(lines);
    }

    [Fact]
    public void IdenticalAdjacentSdkNodesDoNotInvalidateDecreasingRoute()
    {
        var map=new GameMap();var road=AddRoad(map,Point(0),Point(500));
        var entries=new[]{new RouteNode(road.Node.Uid,500,25),new RouteNode(road.Node.Uid,500,25),
            new RouteNode(road.ForwardNode.Uid,0,0)};
        var route=RouteSnapshot.Parse(entries,Now);
        var path=new RoadPathSource(map).GetPath(Point(50),Vector3.UnitX,route,route.Availability);
        Assert.Single(path);
        Assert.Equal(2,route.Nodes.Count); // The ingress removes only the exact adjacent duplicate.
    }
    [Fact]
    public void SameDistanceDifferentUidOrConflictingRepeatedNodeIsNotSilentlyRemoved()
    {
        var map=new GameMap();var road=AddRoad(map,Point(0),Point(500));
        Assert.Throws<InvalidRouteOrderException>(() =>
            Route((road.Node.Uid,500),(999,500),(road.ForwardNode.Uid,0)));
        Assert.Throws<InvalidRouteOrderException>(() =>
            Route((road.Node.Uid,500),(road.Node.Uid,600),(road.ForwardNode.Uid,0)));
    }

    [Fact]
    public void NavigationGraphPreservesRoadTypeInBothTravelDirections()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        road.RoadType = new TruckLib.Token("highway");
        var graph = new RoadPathSource(map).GetNavigationGraph();
        Assert.Equal(2, graph.Count);
        Assert.All(graph, edge => Assert.Equal("highway", edge.RoadType));
        var forward = Assert.Single(graph, edge => edge.StartNodeUid == road.Node.Uid);
        var reverse = Assert.Single(graph, edge => edge.StartNodeUid == road.ForwardNode.Uid);
        Assert.Equal(forward.Points.Reverse(), reverse.Points);
        Assert.Equal(forward.EndNodeUid, reverse.StartNodeUid);
        Assert.Equal(forward.StartNodeUid, reverse.EndNodeUid);
    }

    [Fact]
    public void DescendingRouteNodesIdentifyActualRoadAndTraversalDirection()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var path = new RoadPathSource(map).GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        var selected = Assert.Single(path);
        Assert.Equal(road.Uid.ToString("X16"), selected.Id);
        Assert.Equal(road.Node.Uid, selected.StartNodeUid);
        Assert.Equal(road.ForwardNode.Uid, selected.EndNodeUid);
        Assert.True(Vector3.Distance(Point(0), selected.Points[0]) < 0.01f);
        Assert.True(Vector3.Distance(Point(500), selected.Points[^1]) < 0.01f);
    }

    [Fact]
    public void ReverseRouteReversesRoadGeometryAndEndpointUids()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var route = Route((road.ForwardNode.Uid, 500), (road.Node.Uid, 0));
        var selected = Assert.Single(new RoadPathSource(map).GetPath(Point(450), -Vector3.UnitX, route, route.Availability));
        Assert.Equal(road.ForwardNode.Uid, selected.StartNodeUid);
        Assert.True(Vector3.Distance(Point(500), selected.Points[0]) < 0.01f);
    }

    [Theory]
    [InlineData(RouteAvailability.Unknown)]
    [InlineData(RouteAvailability.Invalid)]
    [InlineData(RouteAvailability.Stale)]
    public void MissingOrStaleRouteDoesNotFallBackToNoRouteCertainty(RouteAvailability availability)
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        Assert.Empty(new RoadPathSource(map).GetPath(Point(50), Vector3.UnitX, route, availability));
    }

    [Fact]
    public void NoRouteChoosesOnlyCurrentRoadAndOrientsItFromVehicleHeading()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        road.Append(Point(1000));
        var route = Route();
        var selected = Assert.Single(new RoadPathSource(map).GetPath(Point(400), -Vector3.UnitX, route, route.Availability));
        Assert.Equal(road.Uid.ToString("X16"), selected.Id);
        Assert.Equal(road.ForwardNode.Uid, selected.StartNodeUid);
    }

    [Fact]
    public void NoRouteAtSimpleJoinRetainsUniqueContinuation()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(250));
        road.Append(Point(500));
        var route = Route();
        Assert.Equal(2, new RoadPathSource(map).GetPath(Point(250), Vector3.UnitX, route, route.Availability).Count);
    }

    [Fact]
    public void NoRouteBetweenEquallyCloseParallelRoadsIsUncertain()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 4), Point(500, 4));
        var route = Route();
        Assert.Empty(new RoadPathSource(map).GetPath(Point(100, 2), Vector3.UnitX, route, route.Availability));
    }

    [Fact]
    public void RouteDoesNotIncludeUnrelatedNearbyRoad()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 20), Point(500, 20));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var selected = Assert.Single(new RoadPathSource(map).GetPath(Point(50), Vector3.UnitX, route, route.Availability));
        Assert.Equal(road.Uid.ToString("X16"), selected.Id);
    }
    [Fact]
    public void CameraOnActualRouteRoadIsConfirmed()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        var path = source.GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.True(source.IsPositionOnPath(Point(350), path));
    }

    [Fact]
    public void NearbyCameraOnParallelNonRouteRoadIsRejectedBeforeProjection()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        var path = source.GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.NotEmpty(path);
        Assert.False(source.IsPositionOnPath(Point(350, 8), path));
    }

    [Fact]
    public void CameraEquallyCloseToParallelRoadsHasNoCertainMembership()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, 8), Point(500, 8));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        var path = source.GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.False(source.IsPositionOnPath(Point(350, 4), path));
    }

    [Fact]
    public void CameraOnUpperNonRouteRoadIsRejected()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(500));
        AddRoad(map, Point(0, y: 8), Point(500, y: 8));
        var route = Route((road.Node.Uid, 500), (road.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        var path = source.GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.False(source.IsPositionOnPath(Point(350, y: 8), path));
    }

    [Theory]
    [InlineData(0,    0, true)]
    [InlineData(0.4f, 0, true)]
    [InlineData(6.6f, 0, false)]
    [InlineData(8.85f,0, false)]
    [InlineData(0,    4, false)]
    public void PlayerUsesActualPrefabLaneWhenAnotherRoadRunsBesideItsRepresentative(float z, float y, bool matches)
    {
        var map = new GameMap();
        AddRoad(map, Point(-10,6.6f), Point(60,6.6f));
        var passage = Passage(42, 1, 2, -5, -5, 0);
        var route = Route((1, 50), (2, 0));
        using var source = new RoadPathSource(map, [passage], roadHalfWidth: _ => 2.25);
        Assert.Equal(matches, source.GetPath(Point(25, z, y), Vector3.UnitX, route, route.Availability).Count == 1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OnlyAPermittedOrdinaryRoadInTheVehiclesDirectionCanDisputeANativeLane(bool forward, bool matches)
    {
        var map = new GameMap();
        AddRoad(map, Point(-10, 1.2f), Point(60, 1.2f));
        var route = Route((1, 50), (2, 0));
        using var source = new RoadPathSource(map, [Passage(42, 1, 2, 0, 0)],
            playerAccess: (_, dir) => dir == forward, roadHalfWidth: _ => 2.25);
        Assert.Equal(matches, source.GetPath(Point(25), Vector3.UnitX, route, route.Availability).Count == 1);
        Assert.Empty(source.GetPath(Point(25), -Vector3.UnitX, route, route.Availability));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public void NearbyPrefabLaneIsComparedEvenWhenNoOrdinaryRoadProjects(float z, bool matches)
    {
        var route = Route((1, 50), (2, 0));
        using var source = new RoadPathSource(new GameMap(),
            [Passage(42, 1, 2, 0, 0), Passage(43, 3, 4, 4, 4)]);
        Assert.Equal(matches, source.GetPath(Point(25, z), Vector3.UnitX, route, route.Availability).Count == 1);
        Assert.Empty(source.GetPath(Point(65, z), Vector3.UnitX, route, route.Availability));
    }

    [Fact]
    public void PathsToDifferentExitsMayUseTheSamePhysicalIncomingCurve()
    {
        var route = Route((1, 50), (2, 0));
        DirectedRoad Branch(ulong exit, int branch, float z)
        {
            var points = new[] { Point(0), Point(10), Point(10), Point(50, z) };
            var lane = new PrefabLanePath(42, 1, exit, 0, 0, 0, 0, 1, 1, [0, branch], points, 50)
                {CurveStartPointIndices = [0,2]};
            return new($"prefab:{42:X16}:1:{exit}",1,exit,points)
                {LanePaths = [lane], NativeEntryTangent = points[1] - points[0], NativeExitTangent = points[^1] - points[^2]};
        }
        using var source = new RoadPathSource(new GameMap(), [Branch(2,1,0), Branch(3,2,8)]);
        Assert.Single(source.GetPath(Point(5), Vector3.UnitX, route, route.Availability));
        Assert.Empty(source.GetPath(Point(45,7), Vector3.UnitX, route, route.Availability));
    }

    [Fact]
    public void NativePlayerProjectionDoesNotAssignACameraToANearbyStationPassage()
    {
        var map = new GameMap();
        AddRoad(map, Point(-10,6.6f), Point(60,6.6f));
        var route = Route((1, 50), (2, 0));
        using var source = new RoadPathSource(map, [Passage(42, 1, 2, 0, 0)], roadHalfWidth: _ => 2.25);
        Assert.Single(source.GetPath(Point(10), Vector3.UnitX, route, route.Availability));
        Assert.Equal(CameraApproach.Unknown, source.MatchCamera(Point(10), Vector3.UnitX,
            new("camera", Point(30,6.6f), Vector3.UnitX), route, route.Availability).Approach);
    }

    [Theory]
    [InlineData( 1.15f,  1.15f,  2.25f, true)]
    [InlineData( 1.15f,  2.25f,  2.25f, false)]
    [InlineData( 1.15f,  1.7f,   2.25f, false)]
    [InlineData( 1.15f,  0,      2.25f, false)]
    [InlineData( 1.15f,  1.15f,  4.5f, false)]
    [InlineData(-2.5f,  -2.5f,  -2.25f, true)]
    [InlineData(-2.5f,  -2.25f, -2.25f, false)]
    [InlineData(-2.5f,  -2.375f,-2.25f, false)]
    public void SingleLaneAnchorsAtBothEndsDistinguishCloseStationAndRoadCenters(float station, float z, float farOffset, bool matches)
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(50));
        DirectedRoad Port(ulong owner, ulong entry, ulong exit, float from, float to, float offset)
        {
            var points = new[] { Point(from,offset), Point(to,offset) };
            var lane = new PrefabLanePath(owner, entry, exit, 0, 0, 0, 0, 1, 1, [0], points, to-from);
            return new($"prefab:{owner:X16}:0:1", entry, exit, [Point(from), Point(to)])
                {LanePaths = [lane]};
        }
        var before = Port(44, 3, road.Node.Uid, -20, 0, MathF.CopySign(2.25f,farOffset));
        var after = Port(45, road.ForwardNode.Uid, 4, 50, 70, farOffset);
        using var source = new RoadPathSource(map, [before, after, Passage(42,1,2,station,station)],
            roadHalfWidth: _ => 4.5) {RoadLaneAccess = (_,_) => [true]};
        var route = Route((1,50),(2,0));
        Assert.Equal(matches, source.GetPath(Point(25,z), Vector3.UnitX, route, route.Availability).Count == 1);
    }

    [Fact]
    public void ConsumedParallelPrefabCannotSupplyEvidenceForALaterVisit()
    {
        using var source = new RoadPathSource(new GameMap(), [Passage(42,1,2,0,0), Passage(43,2,3,0,8)]);
        var sdk = Route((1,100),(2,50),(3,0));
        var matched = source.MatchRoutePosition(Point(25), Vector3.UnitX, sdk, currentNodeIndex: 2);
        Assert.Empty(matched.Path);
        Assert.Null(matched.AlongMeters);
    }

    [Fact]
    public void RoadUsedInTheOppositeDirectionCannotSupplyEvidenceForANearbyRouteExit()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(50));
        using var source = new RoadPathSource(map, [Passage(42, road.Node.Uid, 3, 7, 7)]);
        var route = Route((road.ForwardNode.Uid, 100), (road.Node.Uid, 50), (3, 0));
        Assert.Empty(source.MatchRoutePosition(Point(25), Vector3.UnitX, route).Path);
    }

    [Fact]
    public void RepeatedPhysicalLaneCanSupplyEvidenceInItsNextLiveVisit()
    {
        var forward = Passage(42, 1, 2, 0, 0);
        var back = new DirectedRoad("prefab:000000000000002B:1:0", 2, 1, [Point(50),Point(0)]);
        using var source = new RoadPathSource(new GameMap(), [forward, back]);
        var sdk = Route((1,150),(2,100),(1,50),(2,0));
        var matched = source.MatchRoutePosition(Point(25), Vector3.UnitX, sdk, currentNodeIndex: 3);
        Assert.Equal(3, matched.Path.Count);
        Assert.Equal(125, matched.AlongMeters);
    }

    [Theory]
    [InlineData(0, .25f, true)]
    [InlineData(5, 0, false)]
    public void SharedNativeCurveUsesTheSelectedPassagesGrade(float routeY, float otherY, bool matches)
    {
        DirectedRoad Graded(ulong exit, float y)
        {
            var points = new[] { Point(0, y:y), Point(50, y:y) };
            var lane = new PrefabLanePath(42, 1, exit, 0, 0, 0, 0, 1, 1, [7], points, 50);
            return new($"prefab:{42:X16}:1:{exit}", 1, exit, points) {LanePaths = [lane]};
        }
        using var source = new RoadPathSource(new GameMap(), native: new NativeLaneGraph([Graded(2,routeY), Graded(3,otherY)]));
        var sdk = Route((1,50), (2,0));
        Assert.Equal(matches, source.MatchRoutePosition(Point(25), Vector3.UnitX, sdk).AlongMeters.HasValue);
    }

    [Fact]
    public void ReplacingAnEdgeInAnExistingComponentUpdatesItsLaneMembership()
    {
        var first = Passage(42,1,2,0,0);
        var other = Passage(43,3,4,8,8);
        using var source = new RoadPathSource(new GameMap(), native: new NativeLaneGraph([first,other]));
        var sdk = Route((1,50),(2,0));
        var component = Assert.Single(source.GetRouteComponents(sdk));
        Assert.Single(source.MatchRoutePosition(Point(25), Vector3.UnitX, sdk).Path);
        component[0] = other;
        Assert.Empty(source.MatchRoutePosition(Point(25), Vector3.UnitX, sdk).Path);
        Assert.Single(source.MatchRoutePosition(Point(25,8), Vector3.UnitX, sdk).Path);
    }

    [Theory]
    [InlineData(0,     true,  true)]
    [InlineData(.125f, true,  false)]
    [InlineData(.25f,  true,  false)]
    [InlineData(0,     false, false)]
    public void NearASplitOnlyTheBetterFittingLaneCanResolveTheSdkPassage(float z, bool sharedPort, bool matches)
    {
        var first = Passage(42,1,2,0,0);
        var owner = sharedPort ? 42UL : 43UL;
        var from = sharedPort ? 1UL : 4UL;
        var points = new[] {Point(0),Point(50,.5f)};
        var other = new DirectedRoad($"prefab:{owner:X16}:0:2", from, 3, points)
        {
            LanePaths = [new(owner,from,3,0,0,0,0,1,1,[9],points,50)]
        };
        using var source = new RoadPathSource(new GameMap(), native: new NativeLaneGraph([first,other]));
        var route = Route((1,50),(2,0));
        Assert.Equal(matches, source.MatchRoutePosition(Point(25,z), Vector3.UnitX, route).AlongMeters.HasValue);
    }

    [Theory]
    [InlineData(-2.2f, true)]
    [InlineData(1.75f, false)]
    public void DistinctSameSidePortOffsetsBoundThePossibleOrdinaryLane(float z, bool matches)
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(50));
        DirectedRoad Anchor(ulong owner, ulong from, ulong to, float x, float offset)
        {
            var points = new[] {Point(x,offset), Point(x+10,offset)};
            var lane = new PrefabLanePath(owner,from,to,0,0,0,0,1,1,[0],points,10);
            return new($"prefab:{owner:X16}:0:1",from,to,points){LanePaths = [lane]};
        }
        using var source = new RoadPathSource(map, [
            Anchor(44, 3, road.Node.Uid, -10, .75f),
            Anchor(45, road.ForwardNode.Uid, 4, 50, 2.24f), Passage(42,1,2,-2.2f,-2.2f,1.9f)
        ]){RoadLaneAccess = (_,_) => [true]};
        Assert.Equal(matches, source.MatchRoutePosition(Point(25,z), Vector3.UnitX, Route((1,50),(2,0))).AlongMeters.HasValue);
    }

    private static RouteSnapshot GapRoute(Road first, Road second) => Route(
        (first.Node.Uid, 500), (first.ForwardNode.Uid, 300),
        (second.Node.Uid, 250), (second.ForwardNode.Uid, 0));

    [Fact]
    public void NoRouteExtendsThroughUniquelyConnectedRoadItems()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = first.Append(Point(500));
        var route = Route();
        var source = new RoadPathSource(map);
        var path = source.GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.Equal(2, path.Count);
        Assert.Equal(second.Uid.ToString("X16"), path[1].Id);
        var match = source.MatchCamera(Point(50), Vector3.UnitX,
            new("camera", Point(349), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.UnavoidableAhead, match.Approach);
        Assert.InRange(match.RoadDistanceMeters!.Value, 298.99, 299.01);
    }

    [Fact]
    public void NoRouteDoesNotBridgeNearbyUnconnectedRoadGap()
    {
        var map = new GameMap();
        AddRoad(map, Point(0), Point(200));
        AddRoad(map, Point(250), Point(500));
        var route = Route();
        var source = new RoadPathSource(map);
        Assert.Single(source.GetPath(Point(50), Vector3.UnitX, route, route.Availability));
        var match = source.MatchCamera(Point(50), Vector3.UnitX,
            new("camera", Point(349), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void NoRouteStopsBeforeAmbiguousRoadSuccessor()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        first.Append(Point(500));
        var branch = AddRoad(map, Point(200), Point(200, 300));
        branch.Node = first.ForwardNode;
        var route = Route();
        Assert.Single(new RoadPathSource(map).GetPath(Point(50), Vector3.UnitX, route, route.Availability));
    }

    [Fact]
    public void NoRouteExtensionHasBoundedForwardHorizon()
    {
        var map = new GameMap();
        var road = AddRoad(map, Point(0), Point(100));
        for (int i = 2; i <= 20; i++) road = road.Append(Point(i * 100));
        var route = Route();
        var path = new RoadPathSource(map).GetPath(Point(50), Vector3.UnitX, route, route.Availability);
        Assert.InRange(path.Count, 10, 11);
        Assert.True(path[^1].Points[^1].X <= 1100.1f);
    }

    [Fact]
    public void OrderedRouteGapAllowsDirectFallbackWithoutInventingRoadLength()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = GapRoute(first, second);
        var match = new RoadPathSource(map).MatchCamera(Point(50), Vector3.UnitX,
            new("camera", Point(349), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.RouteAhead, match.Approach);
        Assert.Null(match.RoadDistanceMeters);
        Assert.InRange(match.EuclideanDistanceMeters, 298.99, 299.01);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RouteGapFallbackStillRequiresBothTravelDirections(bool wrongVehicle)
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = GapRoute(first, second);
        var match = new RoadPathSource(map).MatchCamera(Point(50), wrongVehicle ? -Vector3.UnitX : Vector3.UnitX,
            new("camera", Point(349), wrongVehicle ? Vector3.UnitX : -Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void CameraOnEarlierRouteChunkIsBehindRatherThanDirectFallbackAhead()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = GapRoute(first, second);
        var match = new RoadPathSource(map).MatchCamera(Point(300), Vector3.UnitX,
            new("camera", Point(150), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.Behind, match.Approach);
    }

    [Fact]
    public void RepeatedRouteRoadOccurrencesPreventGuessingGapTraversalOrder()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        var route = Route((first.Node.Uid, 1000), (first.ForwardNode.Uid, 800),
            (second.Node.Uid, 700), (second.ForwardNode.Uid, 500),
            (first.Node.Uid, 200), (first.ForwardNode.Uid, 0));
        var match = new RoadPathSource(map).MatchCamera(Point(50), Vector3.UnitX,
            new("camera", Point(349), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.Unknown, match.Approach);
    }

    [Fact]
    public void CameraOnNonRouteParallelRoadCannotUseGapFallback()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(200));
        var second = AddRoad(map, Point(250), Point(500));
        AddRoad(map, Point(250, 8), Point(500, 8));
        var route = GapRoute(first, second);
        var match = new RoadPathSource(map).MatchCamera(Point(50), Vector3.UnitX,
            new("camera", Point(349, 8), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.Unknown, match.Approach);
    }
    [Theory]
    [InlineData(248)]
    [InlineData(250)]
    [InlineData(252)]
    public void RouteMembershipAndDistanceStayContinuousAcrossSimpleJoin(float x)
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        var second = first.Append(Point(500));
        var route = Route((first.Node.Uid, 500), (first.ForwardNode.Uid, 250), (second.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        var match = source.MatchCamera(Point(x, 3), Vector3.UnitX,
            new("camera", Point(400, 3), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.RouteAhead, match.Approach);
        Assert.Equal(400 - x, match.RoadDistanceMeters!.Value, 2);
        var cameraAtJoin = source.MatchCamera(Point(50, 3), Vector3.UnitX,
            new("camera", Point(x, 3), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.RouteAhead, cameraAtJoin.Approach);
        Assert.Equal(x - 50, cameraAtJoin.RoadDistanceMeters!.Value, 2);
    }

    [Theory]
    [InlineData(248)]
    [InlineData(250)]
    [InlineData(252)]
    public void NoRouteAtJoinUsesBothSidesWithoutLosingCameraMembership(float x)
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        first.Append(Point(500));
        var route = Route();
        var source = new RoadPathSource(map);
        var match = source.MatchCamera(Point(x, 3), Vector3.UnitX,
            new("camera", Point(400, 3), Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.UnavoidableAhead, match.Approach);
        Assert.Equal(400 - x, match.RoadDistanceMeters!.Value, 2);
        var reverse = source.MatchCamera(Point(x, 3), -Vector3.UnitX,
            new("camera", Point(50, 3), -Vector3.UnitX), route, route.Availability);
        Assert.Equal(LinkEts2Nav.Core.CameraApproach.UnavoidableAhead, reverse.Approach);
        Assert.Equal(x - 50, reverse.RoadDistanceMeters!.Value, 2);
    }

    [Fact]
    public void JoinWithThirdBranchRemainsUncertainEvenWithNavigation()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        var second = first.Append(Point(500));
        var branch = AddRoad(map, Point(250), Point(250, 200));
        branch.Node = first.ForwardNode;
        var route = Route((first.Node.Uid, 500), (first.ForwardNode.Uid, 250), (second.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        Assert.Empty(source.GetPath(Point(250), Vector3.UnitX, route, route.Availability));
        Assert.Empty(source.GetPath(Point(250), Vector3.UnitX, Route(), RouteAvailability.None));
    }

    [Fact]
    public void NearbyNonRouteRoadCannotBorrowSimpleJoin()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        var second = first.Append(Point(500));
        AddRoad(map, Point(0, 6), Point(500, 6));
        var route = Route((first.Node.Uid, 500), (first.ForwardNode.Uid, 250), (second.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        Assert.Empty(source.GetPath(Point(250, 3), Vector3.UnitX, route, route.Availability));
        Assert.Empty(source.GetPath(Point(250, 3), Vector3.UnitX, Route(), RouteAvailability.None));
    }

    [Fact]
    public void SimpleJoinDoesNotAdmitOnlyOneOfItsRoadsToRoute()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        first.Append(Point(500));
        var route = Route((first.Node.Uid, 250), (first.ForwardNode.Uid, 0));
        var source = new RoadPathSource(map);
        Assert.Empty(source.GetPath(Point(250, 3), Vector3.UnitX, route, route.Availability));
    }

    [Fact]
    public void NoRouteJoinRejectsHeadingPerpendicularToItsLocalTangents()
    {
        var map = new GameMap();
        var first = AddRoad(map, Point(0), Point(250));
        first.Append(Point(250, 250));
        Assert.Empty(new RoadPathSource(map).GetPath(Point(250), Vector3.Normalize(new Vector3(1, 0, -1)), Route(), RouteAvailability.None));
    }
    [Fact]
    public void SingleNodeRetainsTheExistingNoGeometrySemantics()
    {
        var map=new GameMap();var road=AddRoad(map,Point(0),Point(500));
        var route=RouteSnapshot.Parse([new(road.ForwardNode.Uid,0,0)],Now);
        using var source=new RoadPathSource(map);
        Assert.Empty(source.GetRouteComponents(route));Assert.Null(source.GetRouteEntry(route,0));
    }

}
