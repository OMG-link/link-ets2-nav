using System.Diagnostics;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.Core;

internal enum NativeLaneTraceEnd
{
    Port,
    MergeDeath,
    SplitBirth,
    NoMainContinuation,
    InternalEnd,
    Cycle
}

internal sealed record NativeLaneIdentityTrace(NativeLaneTraceEnd End, IReadOnlyList<int> CurveIndices,
    NativeLanePort? Port = null, LaneContinuityPoint? Point = null, LaneContinuityBranch? Branch = null);

/// <summary>Follows the identity of one lane through the original prefab topology.</summary>
internal sealed class NativeLaneIdentityTracer
{
    private readonly ulong prefabUid;
    private readonly Dictionary<int, NativeLaneCurve> curves;
    private readonly Dictionary<(LaneContinuityPointKind, int), LaneContinuityPoint> points;
    private readonly Dictionary<(ulong, int), NativeLanePort> inputs;
    private readonly Dictionary<(ulong, int), NativeLanePort> outputs;
    private readonly ILookup<int, NativeLanePort> inputsByCurve;
    private readonly ILookup<int, NativeLanePort> outputsByCurve;

    public NativeLaneIdentityTracer(NativeLaneGeometry geometry)
    {
        prefabUid = geometry.PrefabUid;
        var continuity = LaneContinuity.Analyze(geometry);
        AssertAnalyzed(continuity.MapIssue);
        curves = geometry.Curves.ToDictionary(curve => curve.Index);
        points = continuity.Points.ToDictionary(point => (point.Kind, point.SharedCurveIndex));
        inputs = geometry.Inputs.ToDictionary(port => (port.NodeUid, port.Slot));
        outputs = geometry.Outputs.ToDictionary(port => (port.NodeUid, port.Slot));
        inputsByCurve = geometry.Inputs.ToLookup(port => port.CurveIndex);
        outputsByCurve = geometry.Outputs.ToLookup(port => port.CurveIndex);
    }

    public NativeLaneIdentityTrace FromInput(ulong node, int slot)
        => Trace(inputs, outputsByCurve, node, slot, reverse: false);

    public NativeLaneIdentityTrace FromOutput(ulong node, int slot)
        => Trace(outputs, inputsByCurve, node, slot, reverse: true);

    private NativeLaneIdentityTrace Trace(Dictionary<(ulong, int), NativeLanePort> starts,
        ILookup<int, NativeLanePort> ends, ulong node, int slot, bool reverse)
    {
#if DEBUG
        if (!starts.ContainsKey((node, slot)))
            throw new NavigationContractException($"Prefab {prefabUid:X16}: no native {(reverse ? "output" : "input")} port at node {node:X16}, slot {slot}.");
#endif
        var current = curves[starts[(node, slot)].CurveIndex];
        var divergence = reverse ? LaneContinuityPointKind.Merge : LaneContinuityPointKind.Split;
        var convergence = reverse ? LaneContinuityPointKind.Split : LaneContinuityPointKind.Merge;
        var trace = new List<int>();
        var visited = new HashSet<int>();
        while (visited.Add(current.Index))
        {
            trace.Add(current.Index);
            var ports = ends[current.Index];
            if (ports.Any())
            {
                if (ports.Skip(1).Any())
                    throw new InvalidDataException($"Prefab {prefabUid:X16}: curve {current.Index} reaches multiple native ports.");
                return new(NativeLaneTraceEnd.Port, trace, Port: ports.First());
            }

            int next;
            if (points.TryGetValue((divergence, current.Index), out var fork))
            {
                AssertAnalyzed(fork.MapIssue);
                var main = fork.Branches.FirstOrDefault(branch => branch.Role == LaneContinuityRole.Survivor);
                if (main is null)
                    return new(NativeLaneTraceEnd.NoMainContinuation, trace, Point: fork);
                next = main.CurveIndex;
            }
            else
            {
                var links = reverse ? current.Previous : current.Next;
                if (links.Count == 0)
                    return new(NativeLaneTraceEnd.InternalEnd, trace);
                next = links[0];
            }

            if (points.TryGetValue((convergence, next), out var join))
            {
                AssertAnalyzed(join.MapIssue);
                var branch = join.Branches.First(candidate => candidate.CurveIndex == current.Index);
                if (branch.Role != LaneContinuityRole.Survivor)
                    return new(reverse ? NativeLaneTraceEnd.SplitBirth : NativeLaneTraceEnd.MergeDeath,
                        trace, Point: join, Branch: branch);
            }
            current = curves[next];
        }
        return new(NativeLaneTraceEnd.Cycle, trace);
    }

    // Remove this assertion when local analysis no longer returns issues.
    [Conditional("DEBUG")]
    private void AssertAnalyzed(MapQueryIssue? issue)
    {
        if (issue is not null)
            throw new NavigationContractException($"Prefab {prefabUid:X16}: {issue.Reason}");
    }
}
