using System.Diagnostics;
using TruckLib.Models.Ppd;

namespace LinkEts2Nav.Map;

/// <summary>Shortest explicitly connected PPD curve chain for a supplied set of entry/exit lanes.</summary>
public static class PrefabCurvePathfinder
{
    private const int MaximumQueueItems = 8192;
    private const float MaximumPathLength = 2500;

    public static IReadOnlyList<int> Find(PrefabDescriptor descriptor, IEnumerable<int> starts, IReadOnlySet<int> targets,
        ulong? prefabUid = null)
        => Search(descriptor, starts, targets, stopAfterFirst: true, prefabUid).Values.FirstOrDefault() ?? [];

    public static IReadOnlyDictionary<int, IReadOnlyList<int>> FindEach(PrefabDescriptor descriptor, int start,
        IReadOnlySet<int> targets, ulong? prefabUid = null)
        => Search(descriptor, [start], targets, stopAfterFirst: false, prefabUid);

    private static Dictionary<int, IReadOnlyList<int>> Search(PrefabDescriptor descriptor, IEnumerable<int> starts,
        IReadOnlySet<int> targets, bool stopAfterFirst, ulong? prefabUid)
    {
        PrefabDescriptorInput.AssertCurveIndices(descriptor);
        PrefabDescriptorInput.AssertCurveGeometry(descriptor);
        var curves = descriptor.NavCurves;
        var startCurves = starts.Where(index => index != -1).Distinct().ToArray();
        AssertSearchPorts(curves.Count, startCurves, targets);
        var result = new Dictionary<int, IReadOnlyList<int>>();
        if (targets.Count == 0) return result;

        var queue = new PriorityQueue<int, (float Cost, int Index)>();
        var costs = new Dictionary<int, float>();
        var previous = new Dictionary<int, int>();
        foreach (int start in startCurves)
        {
            costs[start] = curves[start].Length;
            previous[start] = -1;
            queue.Enqueue(start, (costs[start], start));
        }

        int visited = 0;
        bool reachedQueueLimit = false, reachedLengthLimit = false;
        while (queue.TryDequeue(out int current, out var priority))
        {
            if (visited++ == MaximumQueueItems)
            {
                reachedQueueLimit = true;
                break;
            }

            if (priority.Cost > costs[current]) continue;
            if (priority.Cost > MaximumPathLength)
            {
                reachedLengthLimit = true;
                continue;
            }

            if (targets.Contains(current))
            {
                var chain = new List<int>();
                for (int index = current; index >= 0; index = previous[index]) chain.Add(index);
                chain.Reverse();
                result[current] = chain.ToArray();
                if (stopAfterFirst || result.Count == targets.Count) return result;
            }

            foreach (int next in curves[current].NextLines)
            {
                float cost = priority.Cost + curves[next].Length;
                if (costs.TryGetValue(next, out float known) && known <= cost) continue;
                costs[next] = cost;
                previous[next] = current;
                queue.Enqueue(next, (cost, next));
            }
        }

        if (reachedQueueLimit || reachedLengthLimit)
        {
            var reachable = PrefabLaneTopology.ReachableCurves(descriptor, startCurves);
            var missing = targets.Where(target => reachable.Contains(target) && !result.ContainsKey(target)).Order().ToArray();
            if (missing.Length != 0)
            {
                string limit = (reachedQueueLimit, reachedLengthLimit) switch
                {
                    (true, true) => $"{MaximumQueueItems} queue items and {MaximumPathLength} m",
                    (true, false) => $"{MaximumQueueItems} queue items",
                    _ => $"{MaximumPathLength} m"
                };
                string prefab = prefabUid?.ToString("X16") ?? "(UID unavailable)";
                throw new NotSupportedException($"Prefab {prefab} curves [{string.Join(',', startCurves)}] to [{string.Join(',', missing)}]: search exceeded {limit} with reachable destinations.");
            }
        }

        return result;
    }

    [Conditional("DEBUG")]
    private static void AssertSearchPorts(int count, IReadOnlyList<int> starts, IReadOnlySet<int> targets)
    {
        if (starts.Concat(targets).Any(index => index < 0 || index >= count))
            throw new Core.NavigationContractException("Native prefab curve search was given an out-of-range port index.");
    }
}
