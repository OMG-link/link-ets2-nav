using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Navigation-only corrections to known map instances and their original PPD layouts.</summary>
internal static class PrefabNavigationOverrides
{
    private static readonly IReadOnlyDictionary<ulong, ParallelLane> instances = new Dictionary<ulong, ParallelLane>
    {
        // Two ordinary lanes connect to the main input and output of this invisible merge,
        // although the native PPD describes only the left through lane and its ramp.
        [0x4F84385F6C870001] = new("/prefab2/fork_temp/invis/invis_r1_mage_left_tmpl.ppd",
            [0x4F84385E34070008, 0x4F84385F83070009, 0x4F84385D1F87000A],
            [new(0, 2, [0, 3, 5]), new(1, 2, [1, 4, 2, 5])], MainPassage: 0, HalfLaneSpacing: 2.25f)
    };

    public static ParallelLane? Find(ulong uid) => instances.GetValueOrDefault(uid);

    internal sealed record Passage(int InputNode, int OutputNode, int[] Curves);

    internal sealed record ParallelLane(string Resource, ulong[] ControlNodes, Passage[] NativePassages,
        int MainPassage, float HalfLaneSpacing)
    {
        public void ValidateInstance(Prefab prefab, string? resource)
        {
            if (resource is null || GameResourceStore.Key(resource) != Resource)
                throw Invalid(prefab, $"expected {Resource}, found {resource ?? "(no definition)"}");
            if (prefab.Nodes.Count != ControlNodes.Length || prefab.Origin >= ControlNodes.Length
                || prefab.Nodes.Where((node, index) => node.Uid != ControlNodes[(index + prefab.Origin) % ControlNodes.Length]).Any())
                throw Invalid(prefab, "control node UIDs no longer match the registered PPD interface mapping");
        }

        public PrefabDescriptor Apply(Prefab prefab, PrefabDescriptor source)
        {
            if (source.Nodes.Count != ControlNodes.Length
                || source.NavCurves.Count != NativePassages.SelectMany(p => p.Curves).Distinct().Count())
                throw Invalid(prefab, "native control node or curve count changed");

            for (int i = 0; i < source.Nodes.Count; i++)
            {
                var node = source.Nodes[i];
                if (!SlotsMatch(node.InputLines, NativePassages.Where(p => p.InputNode == i).Select(p => p.Curves[0]))
                    || !SlotsMatch(node.OutputLines, NativePassages.Where(p => p.OutputNode == i).Select(p => p.Curves[^1]).Distinct()))
                    throw Invalid(prefab, $"native lane slots changed at control node {i}");
            }

            var connections = NativePassages.SelectMany(p => p.Curves.Zip(p.Curves.Skip(1))).Distinct().ToArray();
            for (int i = 0; i < source.NavCurves.Count; i++)
            {
                var curve = source.NavCurves[i];
                if (!curve.NextLines.Order().SequenceEqual(connections.Where(p => p.First == i).Select(p => p.Second).Order())
                    || !curve.PreviousLines.Order().SequenceEqual(connections.Where(p => p.Second == i).Select(p => p.First).Order()))
                    throw Invalid(prefab, $"native connectivity changed at curve {i}");
            }

            var passage = NativePassages[MainPassage];
            var input = source.Nodes[passage.InputNode];
            var output = source.Nodes[passage.OutputNode];
            if (input.InputLines[1] != -1 || output.OutputLines[1] != -1)
                throw Invalid(prefab, "the right lane slot is already occupied");
            var first = source.NavCurves[passage.Curves[0]];
            var last = source.NavCurves[passage.Curves[^1]];
            var start = MirrorEndpoint(prefab, input.Position, first.StartPosition, first.StartRotation);
            var end = MirrorEndpoint(prefab, output.Position, last.EndPosition, last.EndRotation);
            var chord = end - start;
            float length = chord.Length();
            if (!float.IsFinite(length) || length <= 0
                || Vector3.Dot(chord / length, Vector3.Transform(-Vector3.UnitZ, first.StartRotation)) < .9999f
                || Vector3.Dot(chord / length, Vector3.Transform(-Vector3.UnitZ, last.EndRotation)) < .9999f)
                throw Invalid(prefab, "mirrored endpoints and native tangents no longer describe a straight passage");

            int index = source.NavCurves.Count;
            var nodes = source.Nodes.ToList();
            nodes[passage.InputNode] = WithLane(input, index, isInput: true);
            nodes[passage.OutputNode] = WithLane(output, index, isInput: false);
            return new PrefabDescriptor
            {
                Nodes = nodes,
                NavCurves = [..source.NavCurves, new NavCurve
                {
                    LeadsToNodes = first.LeadsToNodes, StartPosition = start, EndPosition = end,
                    StartRotation = first.StartRotation, EndRotation = last.EndRotation, Length = length,
                    SemaphoreId = -1, TrafficRule = first.TrafficRule, AllowedVehicles = first.AllowedVehicles
                }],
                Signs = source.Signs, Semaphores = source.Semaphores, SpawnPoints = source.SpawnPoints,
                MapPoints = source.MapPoints, TriggerPoints = source.TriggerPoints,
                Intersections = source.Intersections, NavNodes = source.NavNodes
            };
        }

        private Vector3 MirrorEndpoint(Prefab prefab, Vector3 center, Vector3 native, Quaternion rotation)
        {
            var heading = Vector3.Transform(-Vector3.UnitZ, rotation);
            var right = Vector3.Normalize(new Vector3(-heading.Z, 0, heading.X));
            // The native endpoint represents the left lane center. In the PPD frame its
            // signed offset from the road reference is -2.25 m at both connectors.
            float lateral = Vector3.Dot(native - center, right);
            if (!float.IsFinite(lateral) || MathF.Abs(lateral + HalfLaneSpacing) > .01f
                || Vector3.Distance(native - center, lateral * right) > .01f)
                throw Invalid(prefab, "native endpoint no longer matches the registered left lane offset");
            return native - 2 * lateral * right;
        }

        private static bool SlotsMatch(int[] slots, IEnumerable<int> curves)
        {
            var expected = curves.ToArray();
            return slots.Length > 1 && slots.Take(expected.Length).SequenceEqual(expected)
                && slots.Skip(expected.Length).All(i => i == -1);
        }

        private static ControlNode WithLane(ControlNode source, int index, bool isInput)
        {
            var input = (int[])source.InputLines.Clone();
            var output = (int[])source.OutputLines.Clone();
            (isInput ? input : output)[1] = index;
            return new ControlNode
            {
                Position = source.Position, Direction = source.Direction, TerrainPoints = source.TerrainPoints,
                InputLines = input, OutputLines = output
            };
        }

        private static InvalidDataException Invalid(Prefab prefab, string reason)
            => new($"Prefab {prefab.Uid:X16} navigation override cannot be applied: {reason}.");
    }
}
