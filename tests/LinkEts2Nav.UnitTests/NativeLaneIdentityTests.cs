using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NativeLaneIdentityTests
{
    [Fact]
    public void MainContinuationSurvivesWhenRepresentativePathUsesShorterSideBranch()
    {
        var prefab = new Prefab
        {
            Uid = 42,
            Nodes = [new Node { Uid = 10, Position = Vector3.Zero },
                new Node { Uid = 20, Position = new(0, 0, -40) }]
        };
        var sideRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var descriptor = new PrefabDescriptor
        {
            Nodes = [new ControlNode { InputLines = [-1, 0], OutputLines = [] },
                new ControlNode { Position = new(0, 0, -40), InputLines = [], OutputLines = [-1, -1, 3] }],
            NavCurves =
            [
                new NavCurve { StartPosition = Vector3.Zero, EndPosition = new(0, 0, -10), Length = 10,
                    StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, NextLines = { 1, 2 } },
                new NavCurve { StartPosition = new(0, 0, -10), EndPosition = new(0, 0, -30), Length = 20,
                    StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, PreviousLines = { 0 }, NextLines = { 3 } },
                new NavCurve { StartPosition = new(0, 0, -10), EndPosition = new(0, 0, -30), Length = 10,
                    StartRotation = sideRotation, EndRotation = sideRotation, PreviousLines = { 0 }, NextLines = { 3 } },
                new NavCurve { StartPosition = new(0, 0, -30), EndPosition = new(0, 0, -40), Length = 10,
                    StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity, PreviousLines = { 1, 2 } }
            ]
        };
        var geometry = PrefabPathGeometry.BuildNavigation(prefab, descriptor, []);
        var representative = Assert.Single(Assert.Single(geometry.Paths).LanePaths);
        Assert.Equal([0, 2, 3], representative.CurveIndices);

        var graph = new NativeLaneGraph([], [geometry.Geometry!]);
        var tracer = new NativeLaneIdentityTracer(graph.PrefabGeometry(prefab.Uid)!);
        var forward = tracer.FromInput(10, 1);
        var backward = tracer.FromOutput(20, 2);

        Assert.Empty(graph.Edges);
        Assert.Equal(NativeLaneTraceEnd.Port, forward.End);
        Assert.Equal(new NativeLanePort(20, 2, 0, 3), forward.Port);
        Assert.Equal([0, 1, 3], forward.CurveIndices);
        Assert.Equal(NativeLaneTraceEnd.Port, backward.End);
        Assert.Equal(new NativeLanePort(10, 1, 0, 0), backward.Port);
        Assert.Equal([3, 1, 0], backward.CurveIndices);
    }

    [Fact]
    public void DeathIsRecordedBeforeSharedCurveSplitsTowardTwoPorts()
    {
        var origin = new Vector3(0, 0, -10);
        var split = new Vector3(0, 0, -20);
        var geometry = WithPorts(
            [Curve(0, Vector3.Zero, origin, [], [2]),
                Curve(1, new(10, 0, 0), origin, [], [2]) with { Blinker = LaneBlinker.Left },
                Curve(2, origin, split, [0, 1], [3, 4]),
                Curve(3, split, new(0, 0, -30), [2], []),
                Curve(4, split, new(10, 0, -30), [2], [])],
            [new(10, 0, 0, 0), new(11, 0, 0, 1)],
            [new(20, 0, 0, 3), new(21, 0, 0, 4)]);
        var tracer = new NativeLaneIdentityTracer(geometry);

        var incoming = tracer.FromInput(11, 0);
        var outgoing = tracer.FromOutput(21, 0);

        Assert.Equal(NativeLaneTraceEnd.MergeDeath, incoming.End);
        Assert.Equal([1], incoming.CurveIndices);
        Assert.Equal(2, incoming.Point?.SharedCurveIndex);
        Assert.Equal(NativeLaneTraceEnd.SplitBirth, outgoing.End);
        Assert.Equal([4], outgoing.CurveIndices);
        Assert.Equal(2, outgoing.Point?.SharedCurveIndex);
    }

    [Fact]
    public void EqualBranchesHaveNoMainContinuation()
    {
        var split = new Vector3(0, 0, -10);
        var tracer = new NativeLaneIdentityTracer(WithPorts(
            [Curve(0, Vector3.Zero, split, [], [1, 2]),
                Curve(1, split, new(-10, 0, -20), [0], []),
                Curve(2, split, new(10, 0, -20), [0], [])],
            [new(10, 0, 0, 0)],
            [new(20, 0, 0, 1), new(21, 0, 0, 2)]));

        var trace = tracer.FromInput(10, 0);

        Assert.Equal(NativeLaneTraceEnd.NoMainContinuation, trace.End);
        Assert.Equal(0, trace.Point?.SharedCurveIndex);
        Assert.Equal([0], trace.CurveIndices);
        Assert.Null(trace.Port);
    }

    [Fact]
    public void InternalEndIsNotAnOutlet()
    {
        var prefab = new Prefab { Uid = 42, Nodes = [new Node { Uid = 10, Rotation = Quaternion.Identity }] };
        var descriptor = new PrefabDescriptor
        {
            Nodes = [new ControlNode { Direction = -Vector3.UnitZ, InputLines = [0], OutputLines = [] }],
            NavCurves = [new NavCurve { StartPosition = Vector3.Zero, EndPosition = new(0, 0, -10), Length = 10,
                StartRotation = Quaternion.Identity, EndRotation = Quaternion.Identity }]
        };
        var navigation = PrefabPathGeometry.BuildNavigation(prefab, descriptor, []);

        Assert.Empty(navigation.Paths);
        var graph = new NativeLaneGraph(navigation.Paths, [navigation.Geometry!]);
        var trace = new NativeLaneIdentityTracer(graph.PrefabGeometry(prefab.Uid)!).FromInput(10, 0);
        Assert.Equal(NativeLaneTraceEnd.InternalEnd, trace.End);
        Assert.Equal([0], trace.CurveIndices);
        Assert.Null(trace.Port);
    }

    [Fact]
    public void ClosedContinuationKeepsItsCycleSeparateFromAnInternalEnd()
    {
        var turn = new Vector3(0, 0, -10);
        var tracer = new NativeLaneIdentityTracer(WithPorts(
            [Curve(0, Vector3.Zero, turn, [1], [1]),
                Curve(1, turn, Vector3.Zero, [0], [0])],
            [new(10, 0, 0, 0)], []));

        var trace = tracer.FromInput(10, 0);

        Assert.Equal(NativeLaneTraceEnd.Cycle, trace.End);
        Assert.Equal([0, 1], trace.CurveIndices);
        Assert.Null(trace.Port);
    }

    private static NativeLaneCurve Curve(int index, Vector3 start, Vector3 end, int[] previous, int[] next)
        => new(index, start, end, end - start, end - start, next, previous);

    private static NativeLaneGeometry WithPorts(NativeLaneCurve[] curves, NativeLanePort[] inputs, NativeLanePort[] outputs)
        => new(42, curves)
        {
            Inputs = inputs,
            Outputs = outputs,
            InputCurveIndices = inputs.Select(port => port.CurveIndex).ToArray(),
            OutputCurveIndices = outputs.Select(port => port.CurveIndex).ToArray()
        };
}
