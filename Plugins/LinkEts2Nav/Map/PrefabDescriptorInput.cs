using System.Diagnostics;
using System.Numerics;
using LinkEts2Nav.Core;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Validates raw navigation data before the descriptor is cached or projected.</summary>
internal static class PrefabDescriptorInput
{
    public static void Validate(string resource, PrefabDescriptor descriptor)
    {
        var curves = descriptor.NavCurves;
        if (InvalidCurveIndex(descriptor) is { } indexProblem)
            throw Invalid(indexProblem);
        for (int node = 0; node < descriptor.Nodes.Count; node++)
        {
            var control = descriptor.Nodes[node];
            if (!Finite(control.Position) || !Finite(control.Direction))
                throw Invalid($"control node {node} has a nonfinite position or direction");
        }
        for (int index = 0; index < curves.Count; index++)
        {
            var curve = curves[index];
            if (!float.IsFinite(curve.Length) || curve.Length < 0.001f)
                throw Invalid($"curve {index} has invalid length {curve.Length}");
            if (!Finite(curve.StartPosition) || !Finite(curve.EndPosition))
                throw Invalid($"curve {index} has a nonfinite endpoint");
            if (!FiniteRotation(curve.StartRotation) || !FiniteRotation(curve.EndRotation))
                throw Invalid($"curve {index} has a nonfinite endpoint rotation");
            if (!Enum.IsDefined(curve.Blinker))
                throw Invalid($"curve {index} has unknown Blinker value {(int)curve.Blinker}");
            CheckLinks(curve.NextLines, index, next: true);
            CheckLinks(curve.PreviousLines, index, next: false);
        }

        void CheckLinks(IEnumerable<int> indices, int curve, bool next)
        {
            string direction = next ? "NextLines" : "PreviousLines";
            foreach (int index in indices)
            {
                var opposite = next ? curves[index].PreviousLines : curves[index].NextLines;
                if (!opposite.Contains(curve))
                    throw Invalid($"curve {curve} {direction} index {index} is not reciprocal");
            }
        }

        InvalidDataException Invalid(string reason) => new($"PPD {resource}: {reason}.");
    }

    public static void ValidateInstance(string resource, Prefab prefab, PrefabDescriptor descriptor)
    {
        if (InvalidInstance(prefab, descriptor) is { } problem)
            throw new InvalidDataException($"PPD {resource}, prefab {prefab.Uid:X16}: {problem}.");
    }

    [Conditional("DEBUG")]
    public static void AssertInstance(Prefab prefab, PrefabDescriptor descriptor)
    {
        if (InvalidInstance(prefab, descriptor) is { } problem)
            throw new NavigationContractException($"Prefab {prefab.Uid:X16}: {problem}.");
    }

    [Conditional("DEBUG")]
    public static void AssertCurveIndices(PrefabDescriptor descriptor)
    {
        if (InvalidCurveIndex(descriptor) is { } problem)
            throw new NavigationContractException($"PPD navigation descriptor: {problem}.");
    }

    [Conditional("DEBUG")]
    public static void AssertBlinkers(PrefabDescriptor descriptor)
    {
        for (int index = 0; index < descriptor.NavCurves.Count; index++)
            if (!Enum.IsDefined(descriptor.NavCurves[index].Blinker))
                throw new NavigationContractException(
                    $"PPD curve {index}: unknown Blinker value {(int)descriptor.NavCurves[index].Blinker}.");
    }

    [Conditional("DEBUG")]
    public static void AssertCurveGeometry(PrefabDescriptor descriptor)
    {
        for (int index = 0; index < descriptor.NavCurves.Count; index++)
        {
            var curve = descriptor.NavCurves[index];
            if (!Finite(curve.StartPosition) || !Finite(curve.EndPosition))
                throw new NavigationContractException($"PPD curve {index}: nonfinite endpoint.");
            if (!FiniteRotation(curve.StartRotation) || !FiniteRotation(curve.EndRotation))
                throw new NavigationContractException($"PPD curve {index}: nonfinite endpoint rotation.");
        }
    }

    [Conditional("DEBUG")]
    public static void AssertControlPlacement(PrefabDescriptor descriptor)
    {
        for (int i = 0; i < descriptor.Nodes.Count; i++)
            if (!Finite(descriptor.Nodes[i].Position) || !Finite(descriptor.Nodes[i].Direction))
                throw new NavigationContractException($"PPD control node {i}: nonfinite position or direction.");
    }

    private static string? InvalidInstance(Prefab prefab, PrefabDescriptor descriptor)
    {
        if (descriptor.Nodes.Count < 1 || prefab.Nodes.Count != descriptor.Nodes.Count || prefab.Origin >= descriptor.Nodes.Count)
            return $"instance has {prefab.Nodes.Count} nodes and origin {prefab.Origin}, descriptor has {descriptor.Nodes.Count} nodes";
        var seen = new HashSet<ulong>();
        foreach (var node in prefab.Nodes)
        {
            if (!seen.Add(node.Uid))
                return $"interface node {node.Uid:X16} occurs in multiple slots";
            if (!Finite(node.Position) || !FiniteRotation(node.Rotation))
                return $"node {node.Uid:X16} has nonfinite position or rotation";
        }
        return null;
    }

    private static string? InvalidCurveIndex(PrefabDescriptor descriptor)
    {
        int count = descriptor.NavCurves.Count;
        bool InRange(int index) => index >= 0 && index < count;
        for (int i = 0; i < descriptor.Nodes.Count; i++)
        {
            var control = descriptor.Nodes[i];
            foreach (int index in control.InputLines)
                if (index != -1 && !InRange(index)) return $"control node {i} has invalid input curve index {index}";
            foreach (int index in control.OutputLines)
                if (index != -1 && !InRange(index)) return $"control node {i} has invalid output curve index {index}";
        }
        for (int i = 0; i < count; i++)
        {
            foreach (int index in descriptor.NavCurves[i].NextLines)
                if (!InRange(index)) return $"curve {i} has invalid NextLines index {index}";
            foreach (int index in descriptor.NavCurves[i].PreviousLines)
                if (!InRange(index)) return $"curve {i} has invalid PreviousLines index {index}";
        }
        return null;
    }

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    private static bool FiniteRotation(Quaternion rotation)
        => float.IsFinite(rotation.X) && float.IsFinite(rotation.Y)
            && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);
}
