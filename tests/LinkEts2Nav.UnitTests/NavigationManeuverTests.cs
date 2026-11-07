using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationManeuverTests
{
    private static Vector3 P(float x, float z) => new(x, 0, z);
    private static DirectedRoad R(string id, ulong start, ulong end, params Vector3[] points)
        => new(id, start, end, points);
    private static readonly NavigationManeuverDetector Detector = new();

    [Theory]
    [InlineData(-100, ManeuverKind.TurnLeft)]
    [InlineData(100, ManeuverKind.TurnRight)]
    public void IntersectionUsesTravelRelativeLeftAndRight(float x, ManeuverKind expected)
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var turn = R("turn", 2, 3, P(0, -100), P(x, -100));
        var straight = R("straight", 2, 4, P(0, -100), P(0, -200));
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
        var nearOther = R("prefab:NEAR:0:2", 2, 4, P(0, 0), P(40, 0));
        var middle = R("middle", 3, 5, P(0, -40), P(0, -140));
        var farTurn = R("far-turn", 5, 6, P(0, -140), P(-100, -140));
        var farStraight = R("far-straight", 5, 7, P(0, -140), P(0, -240));
        var graph = new List<DirectedRoad> { entry, near, nearOther, middle, farTurn, farStraight };
        if (invalidAlternativeDirection)
            graph.Add(R("vertical-exit", 4, 8, P(40, 0), new Vector3(40, 20, 0)));
        // The other near exit is either missing, or has no usable horizontal direction.
        Assert.Null(Detector.FindNext([entry, near, middle, farTurn], 0, graph));
    }

    [Fact]
    public void OrderedSequenceContainsStraightThenLeftAndFindNextRemainsCompatible()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var middle = R("middle", 2, 3, P(0, -100), P(0, -200));
        var firstSide = R("first-side", 2, 4, P(0, -100), P(100, -100));
        var selected = R("selected", 3, 5, P(0, -200), P(-100, -200));
        var secondStraight = R("second-straight", 3, 6, P(0, -200), P(0, -300));
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
        var middle = R("middle", 2, 3, P(0, -100), P(0, -200));
        var firstSide = R("first-side", 2, 4, P(0, -100), P(100, -100));
        var beyond = R("beyond", 3, 5, P(0, -200), P(0, -300));
        var brokenSide = R("broken-side", 3, 6, P(10, -200), P(100, -200));
        var farTurn = R("far-turn", 5, 7, P(0, -300), P(-100, -300));
        var farStraight = R("far-straight", 5, 8, P(0, -300), P(0, -400));
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
        var brokenNearExit = R("near-other", 2, 4, P(10, 0), P(100, 0));
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
        var farTurn = R("far-turn", 3, 5, P(-100, 0), P(-100, -100));
        var farStraight = R("far-straight", 3, 6, P(-100, 0), P(-200, 0));
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
        var laterTurn = R("later-turn", 5, 7, P(-141, -1), P(-141, -101));
        var laterStraight = R("later-straight", 5, 8, P(-141, -1), P(-241, -1));
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
            P(0, 0), P(10, 0), P(10, -40), P(0, -40));
        var alternative = R("prefab:SIMPLE:0:2", 2, 4, P(0, 0), P(0, -50));
        var exit = R("exit", 3, 5, P(0, -40), P(-100, -40));
        var straight = R("straight", 4, 6, P(0, -50), P(0, -150));
        var instruction = Detector.FindNext([entry, selected, exit], 0,
            [entry, selected, alternative, exit, straight]);
        Assert.Equal(ManeuverKind.TurnLeft, instruction!.Kind);
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
        var chosen = R("chosen", 2, 3, P(0, -100), P(x, -200));
        var other = R("other", 2, 4, P(0, -100), P(-x, -200));
        Assert.Equal(expected, Detector.FindNext([entry, chosen], 0, [entry, chosen, other])!.Kind);
    }

    [Fact]
    public void ThroughRoadWithPerpendicularAlternativeIsStraight()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var chosen = R("chosen", 2, 3, P(0, -100), P(0, -200));
        var other = R("other", 2, 4, P(0, -100), P(100, -100));
        Assert.Equal(ManeuverKind.Straight, Detector.FindNext([entry, chosen], 0, [entry, chosen, other])!.Kind);
    }

    [Fact]
    public void PrefabUsesExternalRoadTangentsInsteadOfShortLaneReferenceJoins()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        // The initial tiny lane-reference segment points right, despite the complete exit being left.
        var selected = R("prefab:ABC:0:1", 2, 3, P(0, -100), P(2, -100), P(-40, -140));
        var alternative = R("prefab:ABC:0:2", 2, 4, P(0, -100), P(0, -140));
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
    public void UnknownPrefabAlternativeCannotBeUsedToInventAnInstruction()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var chosen = R("prefab:A:0:1", 2, 3, P(0, -100), P(0, -140));
        var other = R("prefab:A:0:2", 2, 4, P(0, -100), P(40, -100));
        var exit = R("exit", 3, 5, P(0, -140), P(0, -240));
        Assert.Null(Detector.FindNext([entry, chosen, exit], 0, [entry, chosen, other, exit]));
    }

    [Fact]
    public void MissingGeometryAndInvalidOffsetsRemainUnknown()
    {
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var gap = R("exit", 2, 3, P(0, -110), P(0, -200));
        var alternative = R("other", 2, 4, P(0, -100), P(100, -100));
        Assert.Null(Detector.FindNext([entry, gap], 0, [entry, gap, alternative]));
        Assert.Null(Detector.FindNext([entry, gap], double.NaN, [entry, gap, alternative]));
    }

    [Fact]
    public void PassedDecisionIsSkippedAndStableIdDoesNotDependOnRoutePrefix()
    {
        var prefix = R("prefix", 7, 1, P(0, 100), P(0, 0));
        var entry = R("entry", 1, 2, P(0, 0), P(0, -100));
        var exit = R("exit", 2, 3, P(0, -100), P(100, -100));
        var other = R("other", 2, 4, P(0, -100), P(0, -200));
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
        var reversal = R("reversal", 2, 3, P(0, -100), P(1, 0));
        var other = R("other", 2, 4, P(0, -100), P(0, -200));
        Assert.Equal(ManeuverKind.UTurn, Detector.FindNext([entry, reversal], 0, [entry, reversal, other])!.Kind);
    }
}
