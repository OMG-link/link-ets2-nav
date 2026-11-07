using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>
/// Country evidence on road/prefab connected components. Build and AddSeed are intended for
/// the scanner's single worker; publish the completed index only after seeding is finished.
/// The index owns only UIDs, component numbers and country strings, never map references.
/// </summary>
public sealed class RoadCountryIndex
{
    private sealed class Evidence
    {
        public string? Country;
        public bool Uncertain;
    }

    private readonly Dictionary<ulong, int> components;
    private readonly Dictionary<int, Evidence> evidence;

    private RoadCountryIndex(Dictionary<ulong, int> components)
    {
        this.components = components;
        evidence = components.Values.Distinct().ToDictionary(component => component, _ => new Evidence());
    }

    public bool Contains(ulong itemUid) => components.ContainsKey(itemUid);

    /// <summary>
    /// True only for a retained component with conflicting or unknown-border evidence.
    /// A component with no country evidence, or an item absent from the index, is not uncertain.
    /// Callers must preserve this distinction before considering a separate country fallback.
    /// </summary>
    public bool IsUncertain(ulong itemUid)
        => components.TryGetValue(itemUid, out int component) && evidence[component].Uncertain;
    public string? GetCountry(ulong itemUid)
        => components.TryGetValue(itemUid, out int component) && !evidence[component].Uncertain
            ? evidence[component].Country : null;

    /// <summary>
    /// Adds explicit evidence, such as an actual road point within a named city's area.
    /// Missing items are ignored. Conflicting or unknown border evidence is permanent;
    /// adding another city or border seed can never make that component certain again.
    /// </summary>
    public void AddSeed(ulong itemUid, string country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        if (!components.TryGetValue(itemUid, out int component)) return;
        var state = evidence[component];
        if (state.Country is not null && !StringComparer.Ordinal.Equals(state.Country, country))
            state.Uncertain = true;
        state.Country ??= country;
    }

    private void MarkUnknown(ulong itemUid)
    {
        if (components.TryGetValue(itemUid, out int component))
            evidence[component].Uncertain = true;
    }

    public static RoadCountryIndex Build(GameMap map, IReadOnlyDictionary<byte, string> countryNames,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(countryNames);
        token.ThrowIfCancellationRequested();

        var items = new Dictionary<ulong, MapItem>();
        foreach (var item in map.MapItems.Values)
        {
            token.ThrowIfCancellationRequested();
            if (item is Road or Prefab && item.Uid != 0) items.TryAdd(item.Uid, item);
        }
        var ids = items.Keys.Select((uid, index) => (uid, index))
            .ToDictionary(pair => pair.uid, pair => pair.index);
        var parents = Enumerable.Range(0, ids.Count).ToArray();
        var ranks = new byte[parents.Length];

        int Find(int item)
        {
            while (parents[item] != item)
            {
                parents[item] = parents[parents[item]];
                item = parents[item];
            }
            return item;
        }
        void Union(int first, int second)
        {
            first = Find(first);
            second = Find(second);
            if (first == second) return;
            if (ranks[first] < ranks[second]) (first, second) = (second, first);
            parents[second] = first;
            if (ranks[first] == ranks[second]) ranks[first]++;
        }

        var seenNodes = new HashSet<ulong>();
        var seeds = new List<(ulong Item, byte Country)>();
        void Visit(INode? input)
        {
            token.ThrowIfCancellationRequested();
            // UnresolvedNode exposes throwing properties; it cannot prove connectivity.
            if (input is not Node node || !seenNodes.Add(node.Uid)) return;
            // An unresolved/dropped item can supply its UID only. Accept it solely if
            // that UID belongs to an actual road/prefab retained in this map.
            ulong backward = node.BackwardItem?.Uid ?? 0;
            ulong forward = node.ForwardItem?.Uid ?? 0;
            bool hasBackward = ids.TryGetValue(backward, out int backwardIndex);
            bool hasForward = ids.TryGetValue(forward, out int forwardIndex);
            if (node.IsCountryBorder)
            {
                // These are ITEM sides, not the heading or red/green side of a node.
                // Do not join across a border, even when both country IDs are equal.
                if (hasBackward) seeds.Add((backward, node.BackwardCountry));
                if (hasForward) seeds.Add((forward, node.ForwardCountry));
            }
            else if (hasBackward && hasForward)
                Union(backwardIndex, forwardIndex);
            // Country bytes on ordinary nodes are flags, not country evidence.
        }

        foreach (var item in items.Values)
        {
            token.ThrowIfCancellationRequested();
            if (item is Road road) { Visit(road.Node); Visit(road.ForwardNode); }
            else if (item is Prefab prefab)
                foreach (var node in prefab.Nodes) Visit(node);
        }

        var ownedComponents = new Dictionary<ulong, int>(ids.Count);
        foreach (var pair in ids)
        {
            token.ThrowIfCancellationRequested();
            ownedComponents.Add(pair.Key, Find(pair.Value));
        }
        var result = new RoadCountryIndex(ownedComponents);
        foreach (var seed in seeds)
        {
            token.ThrowIfCancellationRequested();
            // Zero is always unspecified, even if a malformed definition maps it.
            if (seed.Country != 0 && countryNames.TryGetValue(seed.Country, out string? country)
                && !string.IsNullOrWhiteSpace(country))
                result.AddSeed(seed.Item, country);
            else result.MarkUnknown(seed.Item);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
