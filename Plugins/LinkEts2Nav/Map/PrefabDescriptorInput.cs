using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Validates raw navigation data before the descriptor is cached or projected.</summary>
internal static class PrefabDescriptorInput
{
    public static void Validate(string resource, PrefabDescriptor descriptor)
    {
        var curves = descriptor.NavCurves;
        for (int node = 0; node < descriptor.Nodes.Count; node++)
        {
            var control = descriptor.Nodes[node];
            if (!Finite(control.Position) || !Finite(control.Direction))
                throw Invalid($"control node {node} has a nonfinite position or direction");
            CheckPorts(control.InputLines, node, "input");
            CheckPorts(control.OutputLines, node, "output");
        }
        for (int index = 0; index < curves.Count; index++)
        {
            var curve = curves[index];
            if (!float.IsFinite(curve.Length) || curve.Length < 0)
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

        void CheckPorts(IEnumerable<int> indices, int node, string direction)
        {
            foreach (int index in indices)
                if (index != -1 && !IsCurve(index))
                    throw Invalid($"control node {node} has invalid {direction} curve index {index}");
        }

        void CheckLinks(IEnumerable<int> indices, int curve, bool next)
        {
            string direction = next ? "NextLines" : "PreviousLines";
            foreach (int index in indices)
            {
                if (!IsCurve(index))
                    throw Invalid($"curve {curve} has invalid {direction} index {index}");
                var opposite = next ? curves[index].PreviousLines : curves[index].NextLines;
                if (!opposite.Contains(curve))
                    throw Invalid($"curve {curve} {direction} index {index} is not reciprocal");
            }
        }

        bool IsCurve(int index) => index >= 0 && index < curves.Count;
        InvalidDataException Invalid(string reason) => new($"PPD {resource}: {reason}.");
    }

    public static void ValidateInstance(string resource, Prefab prefab, PrefabDescriptor descriptor)
    {
        PrefabPlacement.ValidateTopology(prefab, descriptor);
        foreach (var node in prefab.Nodes)
            if (!Finite(node.Position) || !FiniteRotation(node.Rotation))
                throw new InvalidDataException(
                    $"PPD {resource}, prefab {prefab.Uid:X16}, node {node.Uid:X16}: nonfinite position or rotation.");
    }

    private static bool Finite(Vector3 point)
        => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

    private static bool FiniteRotation(Quaternion rotation)
        => float.IsFinite(rotation.X) && float.IsFinite(rotation.Y)
            && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);
}
