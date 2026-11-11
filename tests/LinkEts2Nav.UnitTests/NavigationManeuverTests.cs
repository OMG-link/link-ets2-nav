using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationManeuverTests
{
    private static Vector3 P(float x, float z) => new(x, 0, z);
    private static DirectedRoad R(string id, ulong start, ulong end, params Vector3[] points)
        => new(id, start, end, points) {
            // Short synthetic connections describe only the outgoing leg;
            // their independent native entry direction is southbound.
            NativeEntryTangent=id.StartsWith("prefab:")&&points.Length<4?-Vector3.UnitZ:null,
            NativeExitTangent=id.StartsWith("prefab:")&&points.Length<4?points[^1]-points[0]:null };
    // Synthetic confirmed crossing: branches share a prefab, with explicit PPD classification.
    private static DirectedRoad J(string id, ulong start, ulong end, params Vector3[] points)
        => R($"prefab:{start}:0:{end}", start, end, points) with { Junction = new(2, 2, 1) };
    private static readonly NavigationManeuverDetector Detector = new();

    [Theory]
    [InlineData(1, ManeuverKind.MergeLeft)]
    [InlineData(-1, ManeuverKind.MergeRight)]
    public void SeparateEntranceJoiningThroughRoadProducesMergeButThroughTrafficDoesNot(int side, ManeuverKind expected)
    {
        var main=R("prefab:MERGE:0:1",1,3,P(0,100),P(0,100),P(0,50),P(0,0),P(0,0));
        var ramp=R("prefab:MERGE:2:1",2,3,P(20*side,100),P(20*side,100),P(20*side,50),P(20*side,0),P(0,0));
        var before=R("ramp",4,2,P(20*side,150),P(20*side,100));
        var through=R("main",5,1,P(0,150),P(0,100));
        var after=R("exit",3,6,P(0,0),P(0,-100));
        LaneConnection[] lanes=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,2),new(2,1,3,2)];
        main=main with { LaneConnections=lanes }; ramp=ramp with { LaneConnections=lanes };
        DirectedRoad[] graph=[before,through,main,ramp,after];
        Assert.Equal(expected,Assert.Single(Detector.FindSequence([before,ramp,after],0,graph)).Kind);
        Assert.Empty(Detector.FindSequence([through,main,after],0,graph));
        Detector.FindSequence([before,ramp,after],55,graph,out _,out bool inside);
        Assert.True(inside);
        Detector.FindSequence([before,ramp,after],171,graph,out _,out bool passed);
        Assert.False(passed);
    }

    [Theory]
    [InlineData(-8,-49)] [InlineData(8,49)]
    public void ExternalRoadBendsDoNotChangeNativeJunctionDirections(double through,double side)
    {
        Vector3 End(double degrees)=>P((float)(100*Math.Sin(degrees*Math.PI/180)),(float)(-100-100*Math.Cos(degrees*Math.PI/180)));
        var entry=R("entry",1,2,P(0,0),P(0,-100));
        var chosen=R("prefab:CROSS:0:1",2,3,P(0,-100),End(through)) with{Junction=new(3,3,1),NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=-Vector3.UnitZ};
        var other=R("prefab:CROSS:0:2",2,4,P(0,-100),End(side)) with{Junction=chosen.Junction,NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=side<0?-Vector3.UnitX:Vector3.UnitX};
        var result=Detector.FindNext([entry,chosen],0,[entry,chosen,other]);
        Assert.Equal(ManeuverKind.Straight,result!.Kind);
    }

    [Fact]
    public void AmbiguousRoadJoinBlocksContinuationWithoutInventingAnAction()
    {
        var main=R("prefab:ADD:0:1",1,3,P(0,100),P(0,100),P(0,50),P(0,0),P(0,0));
        var ramp=R("prefab:ADD:2:1",2,3,P(20,100),P(20,100),P(20,50),P(20,0),P(0,0));
        LaneConnection[] lanes=[new(1,0,3,0),new(2,0,3,1)];
        main=main with{LaneConnections=lanes};ramp=ramp with{LaneConnections=lanes};
        var before=R("ramp",4,2,P(20,150),P(20,100));
        var after=R("exit",3,6,P(0,0),P(0,-2000));
        var result=Detector.FindSequence([before,ramp,after],0,[before,main,ramp,after],out double confirmed,out bool unresolved);
        Assert.Empty(result);
        Assert.True(confirmed>800);
        Assert.True(unresolved);
    }

    [Theory]
    [InlineData(1000, 0, 1000)]
    [InlineData(1000, 500, 1000)]
    [InlineData(1000, 1050, 1000)]
    [InlineData(1000, 1200, double.PositiveInfinity)]
    public void UnresolvedJoinReportsItsPositionRatherThanInvalidatingEarlierRoad(
        float approach, double vehicleAlong, double expected)
    {
        var before = R("road", 4, 1, P(0, 0), P(0, approach));
        LaneConnection[] lanes = [new(1, 0, 3, 0), new(2, 0, 3, 1)];
        var main = R("prefab:ADD:0:1", 1, 3, P(0, approach), P(0, approach),
            P(0, approach+50), P(0, approach+100), P(0, approach+100)) with { LaneConnections = lanes };
        var side = main with { Id = "prefab:ADD:2:1", StartNodeUid = 2 };
        var after = R("exit", 3, 5, P(0, approach+100), P(0, approach+2100));
        Detector.FindSequence([before, main, after], vehicleAlong, [before, main, side, after],
            out _, out bool unresolved, out double firstJoin);
        Assert.Equal(expected, firstJoin);
        Assert.Equal(double.IsFinite(expected), unresolved);
    }

    [Fact]
    public void MultipleLaneCurvesWithSameEntranceAreNotAnEntryToMainRoad()
    {
        var before=R("road",1,2,P(0,100),P(0,50));
        var left=R("prefab:NARROW:0:1",2,3,P(0,50),P(-3,50),P(-1,25),P(0,0),P(0,0));
        var right=R("prefab:NARROW:0:1:lane",2,3,P(0,50),P(3,50),P(1,25),P(0,0),P(0,0));
        var after=R("exit",3,4,P(0,0),P(0,-100));
        Assert.Empty(Detector.FindSequence([before,left,after],0,[before,left,right,after]));
    }

    [Theory]
    [InlineData(.072, 32.684, null, null, null, ManeuverKind.Straight, true)]
    [InlineData(20, -20, "main", "main", "ramp", ManeuverKind.Straight, true)]
    [InlineData(1, 30, "same", "same", "same", ManeuverKind.Straight, true)]
    [InlineData(-20, 20, "same", "same", "same", ManeuverKind.KeepLeft, false)]
    [InlineData(1.77168, 8.65269, "ger18", "ger16", "ger2", ManeuverKind.Straight, true)]
    [InlineData(1, 30, "ramp", "main", "ramp", ManeuverKind.KeepLeft, false)]
    [InlineData(-20, 20, null, null, null, ManeuverKind.KeepLeft, false)]
    public void MainRoadContinuationUsesUniqueTypeBeforeNearStraightGeometry(double selectedAngle,
        double otherAngle, string? entryType, string? selectedType, string? otherType,
        ManeuverKind? expected, bool continuation)
    {
        Vector3 Exit(double angle) => P((float)(100 * Math.Sin(angle * Math.PI / 180)),
            -100 - (float)(100 * Math.Cos(angle * Math.PI / 180)));
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100)) with { RoadType = entryType };
        var selected = J("selected", 2, 3, P(0, -100), Exit(selectedAngle)) with { RoadType = selectedType };
        var other = J("other", 2, 4, P(0, -100), Exit(otherAngle)) with { RoadType = otherType };
        var selectedRoad = R("selected-road", 3, 30, selected.Points[^1], selected.Points[^1] + selected.Points[^1] - selected.Points[0]) with { RoadType = selectedType };
        var otherRoad = R("other-road", 4, 40, other.Points[^1], other.Points[^1] + other.Points[^1] - other.Points[0]) with { RoadType = otherType };
        var result = Detector.FindNext([entry, selected, selectedRoad], 0, [entry, selected, other, selectedRoad, otherRoad]);
        if(expected is null){Assert.Null(result);return;}
        Assert.Equal(expected, result!.Kind);
        Assert.Equal(continuation, result.IsMainRoadContinuation);
    }

    [Fact]
    public void PrefabMainRoadUsesItsExternalRoadTypes()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0)) with { RoadType = "main" };
        var chosen = R("prefab:FORK:0:1", 2, 3, P(0, 0), P(0, -30)) with { Junction = new(2, 2, 1),NativeExitTangent=new(.3f,0,-1) };
        var side = R("prefab:FORK:0:2", 2, 4, P(0, 0), P(30, 0)) with { Junction = new(2, 2, 1),NativeExitTangent=new(-.3f,0,-1) };
        var main = R("main", 3, 5, P(0, -30), P(30, -130)) with { RoadType = "main" };
        var ramp = R("ramp", 4, 6, P(30, 0), P(0, -100)) with { RoadType = "ramp" };
        var result = Detector.FindNext([entry, chosen, main], 0, [entry, chosen, side, main, ramp]);
        Assert.Equal(ManeuverKind.Straight, result!.Kind);
        Assert.True(result.IsMainRoadContinuation);
    }

    [Fact]
    public void SingleLongRoadConfirmsItsEntireLengthWithoutAnInstruction()
    {
        var road = R("highway", 1, 2, P(0, 0), P(0, -2000));
        Assert.Empty(Detector.FindSequence([road], 100, [road], out double confirmed));
        Assert.Equal(2000, confirmed);
    }

    [Fact]
    public void UnknownPrefabConfirmsOnlyItsApproachRoad()
    {
        var road = R("entry", 1, 2, P(0, 0), P(0, -100));
        var unknown = R("prefab:UNKNOWN:0:1", 2, 3, P(0, -100), P(0, -140));
        var beyond = R("beyond", 3, 4, P(0, -140), P(0, -1000));
        Assert.Empty(Detector.FindSequence([road, unknown, beyond], 20, [road, beyond], out double confirmed));
        Assert.Equal(100, confirmed);
    }

    [Fact]
    public void KnownForcedPrefabExtendsConfirmedDistanceThroughFollowingRoad()
    {
        var road = R("entry", 1, 2, P(0, 0), P(0, -100));
        var bridge = R("prefab:KNOWN:0:1", 2, 3, P(0, -100), P(0, -140));
        var beyond = R("beyond", 3, 4, P(0, -140), P(0, -1000));
        DirectedRoad[] route = [road, bridge, beyond];
        Assert.Empty(Detector.FindSequence(route, 20, route, out double confirmed));
        Assert.Equal(1000, confirmed);
    }

    [Fact]
    public void DisconnectedRouteConfirmsNoDistanceEvenBeforeItsGap()
    {
        var first = R("first", 1, 2, P(0, 0), P(0, -100));
        var disconnected = R("other", 3, 4, P(0, -100), P(0, -200));
        Assert.Empty(Detector.FindSequence([first, disconnected], 20, [first, disconnected], out double confirmed));
        Assert.Equal(0, confirmed);
    }

    [Theory]
    [InlineData(6.75f)]
    [InlineData(50f)]
    public void SharedUidConnectsPrefabToRoadDespiteLateralReferenceOffset(float offset)
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var bridge = R("prefab:KNOWN:0:1", 2, 3, P(offset, -100), P(offset, -140));
        var beyond = R("beyond", 3, 4, P(0, -140), P(0, -1000));
        DirectedRoad[] route = [entry, bridge, beyond];
        Assert.Empty(Detector.FindSequence(route, 20, route, out double confirmed));
        Assert.Equal(1000, confirmed);
    }

    [Theory]
    [InlineData(-100, ManeuverKind.TurnLeft)]
    [InlineData(100, ManeuverKind.TurnRight)]
    public void IntersectionUsesTravelRelativeLeftAndRight(float x, ManeuverKind expected)
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var turn = J("turn", 2, 3, P(0, -100), P(x, -100));
        var straight = J("straight", 2, 4, P(0, -100), P(0, -200));
        var result = Detector.FindNext([entry, turn], 25, [entry, turn, straight]);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(75, result.DistanceMeters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownNearPrefabBlocksAnOtherwiseKnownFarTurn(bool invalidAlternativeDirection)
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var near = R("prefab:NEAR:0:1", 2, 3, P(0, 0), P(0, -40));
        var nearOther = invalidAlternativeDirection
            ? R("prefab:NEAR:0:2", 2, 4, P(0, 0), P(40, 0))
            : R("prefab:NEAR:0:2", 2, 4, P(0, 0));
        var middle = R("middle", 3, 5, P(0, -40), P(0, -140));
        var farTurn = R("far-turn", 5, 6, P(0, -140), P(-100, -140));
        var farStraight = R("far-straight", 5, 7, P(0, -140), P(0, -240));
        var graph = new List<DirectedRoad> { entry, near, nearOther, middle, farTurn, farStraight };
        if (invalidAlternativeDirection)
            graph.Add(R("vertical-exit", 4, 8, P(40, 0), new Vector3(40, 20, 0)));
        // The other near exit has invalid curve geometry or no usable horizontal direction.
        Assert.Null(Detector.FindNext([entry, near, middle, farTurn], 0, graph));
    }

    private static DirectedRoad JExitAt(string id, ulong node, double angle)
        => ExitAt(id, node, angle) with { Id = $"prefab:2:0:{node}", Junction = new(2, 2, 1),
            NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=P((float)Math.Sin(angle*Math.PI/180),-(float)Math.Cos(angle*Math.PI/180)) };

    private static DirectedRoad ExitAt(string id,ulong node,double angle)
    {
        double radians = angle * Math.PI / 180;
        return R(id,2,node,P(0,-100),P((float)(100*Math.Sin(radians)),(float)(-100-100*Math.Cos(radians))));
    }

    [Theory]
    [InlineData(1,false)]
    [InlineData(1,true)]
    [InlineData(-1,false)]
    [InlineData(-1,true)]
    public void UnqualifiedCollectorSplitDoesNotUseAngularSideGuidance(int mirror,bool collector)
    {
        var entry=R("entry",1,2,P(0,0),P(0,-100));
        // Measured exit tangents at the cloverleaf: only 6.881 degrees apart.
        var main=ExitAt("main",3,mirror*1.7716804);
        var auxiliary=ExitAt("auxiliary",4,mirror*8.6526888);
        var selected=collector?auxiliary:main;
        var result=Detector.FindNext([entry,selected],10,[entry,main,auxiliary]);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearlyAlignedExitsBelowTheForkSpreadRemainStraight(bool right)
    {
        var entry=R("entry",1,2,P(0,0),P(0,-100));
        var first=JExitAt("first",3,0);
        var second=JExitAt("second",4,4.9);
        Assert.Equal(ManeuverKind.Straight,
            Detector.FindNext([entry,right?second:first],0,[entry,first,second])!.Kind);
    }

    [Fact]
    public void MiddleExitOfAShallowThreeWaySplitDoesNotInventASideInstruction()
    {
        var entry=R("entry",1,2,P(0,0),P(0,-100));
        var left=JExitAt("left",3,0);
        var middle=JExitAt("middle",4,3);
        var right=JExitAt("right",5,6.881);
        Assert.Equal(ManeuverKind.Straight,
            Detector.FindNext([entry,middle],0,[entry,left,middle,right])!.Kind);
    }

    [Fact]
    public void InvalidNativeCurveCannotBorrowLateralReferenceConnectorsForDirection()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var broken = R("prefab:BROKEN:0:1", 2, 3,
            P(0, 0), P(0, 0), new Vector3(0, 10, 0), P(0, -40));
        var side = R("prefab:BROKEN:0:2", 2, 4, P(0, 0), P(40, 0));
        var exit = R("exit", 3, 5, P(0, -40), P(0, -140));
        Assert.Null(Detector.FindNext([entry, broken, exit], 0, [entry, broken, side, exit]));
    }
    [Fact]
    public void NeighboringPrefabMaySupplyAnAlternativeBranchWithoutAnExternalRoad()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var near = R("prefab:NEAR:0:1", 2, 3, P(0, 0), P(0, -40)) with { Junction = new(2, 2, 1) };
        var nearSide = R("prefab:NEAR:0:2", 2, 4, P(0, 0), P(40, 0)) with { Junction = new(2, 2, 1) };
        var sideNeighbor = R("prefab:SIDE:0:1", 4, 8, P(40, 0), P(80, 0)) with { Junction = new(2, 2, 1) };
        var middle = R("middle", 3, 5, P(0, -40), P(0, -140));
        var farTurn = J("far-turn", 5, 6, P(0, -140), P(-100, -140));
        var farStraight = J("far-straight", 5, 7, P(0, -140), P(0, -240));
        var sequence = Detector.FindSequence([entry, near, middle, farTurn], 0,
            [entry, near, nearSide, sideNeighbor, middle, farTurn, farStraight]);
        Assert.Equal(new[] { ManeuverKind.Straight, ManeuverKind.TurnLeft }, sequence.Select(m => m.Kind));
        Assert.Equal("prefab:NEAR", sequence[0].JunctionId);
        Assert.True(sequence[0].HasSeparateApproachRoad);
    }

    [Fact]
    public void AdjacentPrefabTurnsUseNativeCurvesAndAreNotAssumedSeparateCountableIntersections()
    {
        var (route, graph) = AdjacentTurns();
        var sequence = Detector.FindSequence(route, 0, graph);
        Assert.Equal(new[] { ManeuverKind.TurnRight, ManeuverKind.TurnRight }, sequence.Select(m => m.Kind));
        Assert.Equal(new[] { "prefab:FIRST", "prefab:SECOND" }, sequence.Select(m => m.JunctionId));
        Assert.True(sequence[0].HasSeparateApproachRoad);
        Assert.False(sequence[1].HasSeparateApproachRoad);
    }

    [Fact]
    public void AlreadyEnteredOrdinaryPrefabDoesNotHideTheNextDecision()
    {
        var (route, graph) = AdjacentTurns();
        var sequence = Detector.FindSequence(route, 105, graph);
        var next = Assert.Single(sequence);
        Assert.Equal("prefab:SECOND", next.JunctionId);
        Assert.Equal(ManeuverKind.TurnRight, next.Kind);
        Assert.True(next.DistanceMeters > 0);
    }

    [Fact]
    public void GraphReferenceChangeRebuildsItsLookupIndex()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var straight = J("straight", 2, 3, P(0, 0), P(0, -100));
        var side = J("side", 2, 4, P(0, 0), P(100, 0));
        DirectedRoad[] fork = [entry, straight, side];
        DirectedRoad[] forced = [entry, straight];
        Assert.NotNull(Detector.FindNext([entry, straight], 0, fork));
        Assert.Null(Detector.FindNext([entry, straight], 0, forced));
        Assert.NotNull(Detector.FindNext([entry, straight], 0, fork));
    }

    private static (DirectedRoad[] Route, DirectedRoad[] Graph) AdjacentTurns()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var first = R("prefab:FIRST:0:1", 2, 3, P(0, 0), P(0, -1),
            P(0, -5), P(5, -10), P(15, -10), P(20, -10)) with { Junction = new(2, 2, 1) };
        var firstStraight = R("prefab:FIRST:0:2", 2, 4, P(0, 0), P(0, -40)) with { Junction = new(2, 2, 1) };
        var second = R("prefab:SECOND:0:1", 3, 5, P(20, -10), P(21, -10),
            P(25, -10), P(30, -5), P(30, 5), P(30, 10)) with { Junction = new(2, 2, 1) };
        var secondStraight = R("prefab:SECOND:0:2", 3, 6,
            P(20, -10), P(21, -10), P(49, -10), P(50, -10)) with { Junction = new(2, 2, 1) };
        var exit = R("exit", 5, 7, P(30, 10), P(30, 110));
        return ([entry, first, second, exit], [entry, first, firstStraight, second, secondStraight, exit]);
    }

    [Fact]
    public void OrderedSequenceContainsStraightThenLeftAndFindNextRemainsCompatible()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var middle = J("middle", 2, 3, P(0, -100), P(0, -200));
        var firstSide = J("first-side", 2, 4, P(0, -100), P(100, -100));
        var selected = J("selected", 3, 5, P(0, -200), P(-100, -200));
        var secondStraight = J("second-straight", 3, 6, P(0, -200), P(0, -300));
        DirectedRoad[] route = [entry, middle, selected];
        DirectedRoad[] graph = [entry, middle, firstSide, selected, secondStraight];
        var sequence = Detector.FindSequence(route, 10, graph);
        Assert.Equal(new[] { ManeuverKind.Straight, ManeuverKind.TurnLeft }, sequence.Select(m => m.Kind));
        Assert.Equal(new[] { 90d, 190d }, sequence.Select(m => m.DistanceMeters));
        Assert.Equal(sequence[0], Detector.FindNext(route, 10, graph));
    }

    [Fact]
    public void SequenceRetainsKnownEarlierDecisionButStopsAtUnknownNearerChoice()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var middle = J("middle", 2, 3, P(0, -100), P(0, -200));
        var firstSide = J("first-side", 2, 4, P(0, -100), P(100, -100));
        var beyond = J("beyond", 3, 5, P(0, -200), P(0, -300));
        var brokenSide = J("broken-side", 3, 6, P(float.NaN, -200), P(100, -200));
        var farTurn = J("far-turn", 5, 7, P(0, -300), P(-100, -300));
        var farStraight = J("far-straight", 5, 8, P(0, -300), P(0, -400));
        DirectedRoad[] route = [entry, middle, beyond, farTurn];
        DirectedRoad[] graph = [entry, middle, firstSide, beyond, brokenSide, farTurn, farStraight];
        var sequence = Detector.FindSequence(route, 0, graph);
        Assert.Equal(ManeuverKind.Straight, Assert.Single(sequence).Kind);
        Assert.Equal(sequence[0], Detector.FindNext(route, 0, graph));
    }
    [Fact]
    public void InvalidNearOrdinaryForkBlocksALaterKnownTurn()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var middle = R("middle", 2, 3, P(0, 0), P(0, -100));
        var brokenNearExit = R("near-other", 2, 4, P(float.NaN, 0), P(100, 0));
        var farTurn = R("far-turn", 3, 5, P(0, -100), P(-100, -100));
        var farStraight = R("far-straight", 3, 6, P(0, -100), P(0, -200));
        Assert.Null(Detector.FindNext([entry, middle, farTurn], 0,
            [entry, middle, brokenNearExit, farTurn, farStraight]));
    }

    [Fact]
    public void ForcedNearBendCanBeSkippedToFindTheNextActualChoice()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var middle = R("middle", 2, 3, P(0, 0), P(-100, 0));
        var farTurn = J("far-turn", 3, 5, P(-100, 0), P(-100, -100));
        var farStraight = J("far-straight", 3, 6, P(-100, 0), P(-200, 0));
        var result = Detector.FindNext([entry, middle, farTurn], 0,
            [entry, middle, farTurn, farStraight]);
        Assert.NotNull(result);
        Assert.Equal("middle", result.EntryId);
        Assert.Equal(200, result.DistanceMeters);
    }

    [Fact]
    public void ComplexCircularPrefabCannotBecomeANetLeftTurnOrSkipToALaterInstruction()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        var circular = R("prefab:CIRCLE:0:1", 2, 3, P(0, 0), P(0, -1),
            P(40, -1), P(40, -41), P(-40, -41), P(-40, -1), P(-41, -1));
        var alternative = R("prefab:CIRCLE:0:2", 2, 4, P(0, 0), P(0, -60));
        var exit = R("exit", 3, 5, P(-41, -1), P(-141, -1));
        var straight = R("straight", 4, 6, P(0, -60), P(0, -160));
        var laterTurn = J("later-turn", 5, 7, P(-141, -1), P(-141, -101)) with{NativeEntryTangent=-Vector3.UnitX};
        var laterStraight = J("later-straight", 5, 8, P(-141, -1), P(-241, -1)) with{NativeEntryTangent=-Vector3.UnitX};
        DirectedRoad[] route = [entry, circular, exit, laterTurn];
        DirectedRoad[] graph = [entry, circular, alternative, exit, straight, laterTurn, laterStraight];
        Assert.Null(Detector.FindNext(route, 0, graph));
        Assert.Null(Detector.FindNext(route, 105, graph)); // still inside the complex junction
        double circularLength = circular.Points.Zip(circular.Points.Skip(1))
            .Sum(p => (double)Vector3.Distance(p.First, p.Second));
        var after = Detector.FindNext(route, 100 + circularLength + 10, graph);
        Assert.Equal(ManeuverKind.TurnRight, after!.Kind);
        Assert.Equal("exit", after.EntryId);
    }

    [Fact]
    public void LateralLaneReferenceConnectorsDoNotMakeASimplePrefabComplex()
    {
        var entry = R("entry", 1, 2, P(0, 100), P(0, 0));
        // The connectors create two right angles, but the actual internal curve is straight.
        var selected = R("prefab:SIMPLE:0:1", 2, 3,
            P(0, 0), P(10, 0), P(10, -40), P(0, -40)) with { Junction = new(2, 2, 1) };
        var alternative = R("prefab:SIMPLE:0:2", 2, 4, P(0, 0), P(0, -50)) with { Junction = new(2, 2, 1) };
        var exit = R("exit", 3, 5, P(0, -40), P(-100, -40));
        var straight = R("straight", 4, 6, P(0, -50), P(0, -150));
        var instruction = Detector.FindNext([entry, selected, exit], 0,
            [entry, selected, alternative, exit, straight]);
        Assert.Equal(ManeuverKind.Straight, instruction!.Kind);
    }

    [Fact]
    public void ASeparateReverseFacingExitDoesNotCreateAForwardChoice()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var outgoing = R("outgoing", 2, 3, P(0, -100), P(0, -200));
        var reversal = R("separate-return", 2, 4, P(0, -100), P(2, 0));
        Assert.Null(Detector.FindNext([entry, outgoing], 0, [entry, outgoing, reversal]));
    }

    [Fact]
    public void SingleContinuationAndNaturalBendDoNotProduceInstructions()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(100, 0));
        var bend = R("bend", 2, 3, P(100, 0), P(100, 100));
        Assert.Null(Detector.FindNext([entry, bend], 0, [entry, bend]));
    }

    [Theory]
    [InlineData(-40, ManeuverKind.KeepLeft)]
    [InlineData(40, ManeuverKind.KeepRight)]
    public void ShallowForkUsesExitOrderingRatherThanCallingEveryBendATurn(float x, ManeuverKind expected)
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var chosen = R("prefab:CROSS:0:1", 2, 3, P(0, -100), P(x, -200)) with{Junction=new(3,3,1)};
        var other = R("prefab:CROSS:0:2", 2, 4, P(0, -100), P(-x, -200)) with{Junction=new(3,3,1)};
        Assert.Equal(expected, Detector.FindNext([entry, chosen], 0, [entry, chosen, other])!.Kind);
    }

    [Theory]
    [InlineData(2,2,0)]
    [InlineData(1,2,1)]
    [InlineData(2,1,1)]
    public void ForkDoesNotUseAngularSideGuidanceWithoutCrossingJunction(int entries,int exits,int crossings)
    {
        var entry=R("entry",1,2,P(0,0),P(0,-100));
        var chosen=R("prefab:FORK:0:1",2,3,P(0,-100),P(40,-200)) with{Junction=new(entries,exits,crossings)};
        var other=R("prefab:FORK:0:2",2,4,P(0,-100),P(-40,-200)) with{Junction=chosen.Junction};
        Assert.Null(Detector.FindNext([entry,chosen],0,[entry,chosen,other]));
    }

    [Fact]
    public void ThroughRoadWithPerpendicularAlternativeIsStraight()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var chosen = J("chosen", 2, 3, P(0, -100), P(0, -200));
        var other = J("other", 2, 4, P(0, -100), P(100, -100));
        Assert.Equal(ManeuverKind.Straight, Detector.FindNext([entry, chosen], 0, [entry, chosen, other])!.Kind);
    }

    [Fact]
    public void PrefabUsesNativeTangentsInsteadOfExternalRoadsAndReferenceJoins()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        // The initial tiny lane-reference segment points right, despite the complete exit being left.
        var selected = R("prefab:ABC:0:1", 2, 3, P(0, -100), P(2, -100), P(-40, -140)) with { Junction = new(2, 2, 1),NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=-Vector3.UnitX };
        var alternative = R("prefab:ABC:0:2", 2, 4, P(0, -100), P(0, -140)) with { Junction = new(2, 2, 1) };
        var exit = R("left", 3, 5, P(-40, -140), P(-140, -140));
        var straight = R("straight", 4, 6, P(0, -140), P(0, -240));
        var result = Detector.FindNext([entry, selected, exit], 10,
            [entry, selected, alternative, exit, straight]);
        Assert.Equal(ManeuverKind.TurnLeft, result!.Kind);
        Assert.Equal(90, result.DistanceMeters);
    }

    [Fact]
    public void DuplicateLaneConnectionsToSamePrefabExitAreOneChoice()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var first = R("prefab:ABC:0:1", 2, 3, P(0, -100), P(0, -140));
        var duplicate = R("prefab:ABC:0:1", 2, 3, P(0, -100), P(1, -120), P(0, -140));
        var exit = R("exit", 3, 4, P(0, -140), P(0, -240));
        Assert.Null(Detector.FindNext([entry, first, exit], 0, [entry, first, duplicate, exit]));
    }

    [Fact]
    public void ReverseIncomingRoadIsNotAnAdditionalExit()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var reverse = R("entry", 2, 1, P(0, -100), P(0, 0));
        var outgoing = R("outgoing", 2, 3, P(0, -100), P(0, -200));
        Assert.Null(Detector.FindNext([entry, outgoing], 0, [entry, reverse, outgoing]));
    }

    [Fact]
    public void ExplicitPrefabCurveCanConfirmAnExitWithoutAnExternalRoad()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var chosen = R("prefab:A:0:1", 2, 3, P(0, -100), P(0, -140)) with { Junction = new(2, 2, 1) };
        var other = R("prefab:A:0:2", 2, 4, P(0, -100), P(40, -100)) with { Junction = new(2, 2, 1) };
        var exit = R("exit", 3, 5, P(0, -140), P(0, -240));
        Assert.Equal(ManeuverKind.Straight,
            Detector.FindNext([entry, chosen, exit], 0, [entry, chosen, other, exit])!.Kind);
    }

    [Fact]
    public void SharedNodeAllowsEndpointOffsetButInvalidRouteProgressRemainsUnknown()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var gap = J("exit", 2, 3, P(0, -110), P(0, -200));
        var alternative = J("other", 2, 4, P(0, -100), P(100, -100));
        Assert.Equal(ManeuverKind.Straight, Detector.FindNext([entry, gap], 0, [entry, gap, alternative])!.Kind);
        Assert.Null(Detector.FindNext([entry, gap], double.NaN, [entry, gap, alternative]));
    }

    [Fact]
    public void PassedDecisionIsSkippedAndStableIdDoesNotDependOnRoutePrefix()
    {
        var prefix = R("prefix", 7, 1, P(0, 100), P(0, 0));
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var exit = J("exit", 2, 3, P(0, -100), P(100, -100));
        var other = J("other", 2, 4, P(0, -100), P(0, -200));
        var graph = new[] { prefix, entry, exit, other };
        var first = Detector.FindNext([prefix, entry, exit], 50, graph);
        var trimmed = Detector.FindNext([entry, exit], 0, graph);
        Assert.Equal(first!.Id, trimmed!.Id);
        Assert.Null(Detector.FindNext([entry, exit], 101, graph));
    }

    [Fact]
    public void SelectedReversalCanBeIdentifiedAsUTurn()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var reversal = J("reversal", 2, 3, P(0, -100), P(1, 0));
        var other = J("other", 2, 4, P(0, -100), P(0, -200));
        Assert.Equal(ManeuverKind.UTurn, Detector.FindNext([entry, reversal], 0, [entry, reversal, other])!.Kind);
    }
    public static IEnumerable<object?[]> JunctionAngleCases()
    {
        (double Angle, ManeuverKind Kind)[] directions =
            [(-90, ManeuverKind.TurnLeft), (90, ManeuverKind.TurnRight),
             (-20, ManeuverKind.KeepLeft), (20, ManeuverKind.KeepRight),
             (179, ManeuverKind.UTurn), (0, ManeuverKind.Straight)];
        PrefabJunctionInfo?[] classifications = [null, new(2,2,0), new(1,2,1), new(2,1,1), new(2,2,1)];
        foreach (var (angle, kind) in directions)
        foreach (var junction in classifications)
            yield return [angle, kind, junction];
    }

    [Theory]
    [MemberData(nameof(JunctionAngleCases))]
    public void AllAngularDirectionsRequireTheSameConfirmedJunction(
        double angle, ManeuverKind expected, PrefabJunctionInfo? junction)
    {
        var entry = R("entry", 1, 2, P(0,0), P(0,-100));
        var chosen = JExitAt("chosen", 3, angle) with { Junction = junction };
        var other = JExitAt("other", 4, angle == 0 ? 90 : -angle) with { Junction = junction };
        var result = Detector.FindNext([entry, chosen], 0, [entry, chosen, other]);
        if (junction?.IsJunction == true) Assert.Equal(expected, result!.Kind);
        else Assert.Null(result);
    }

}
