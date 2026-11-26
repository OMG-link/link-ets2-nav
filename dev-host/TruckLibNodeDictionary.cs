// Adapted from TruckLib's NodeDictionary under ETS2LA/TruckLib/LICENSE.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using RBush;

namespace TruckLib.ScsMap.Collections;

/// <summary>Native node lookup by UID and by bounding box.</summary>
public class NodeDictionary : IDictionary<ulong, INode>
{
    private readonly Dictionary<ulong, INode> dictionary = [];
    private volatile RBush<Node> spatialIndex = AppContext.TryGetSwitch("LinkEts2Nav.NavigationOnly", out bool enabled) && enabled
        ? null : new RBush<Node>();

    // Position updates maintain an existing index. Only a spatial query builds a deferred one.
    internal RBush<Node> Tree => spatialIndex;

    public INode this[ulong key]
    {
        get => dictionary[key];
        set
        {
            var previous = (Node)dictionary[key];
            var replacement = (Node)value;
            ArgumentNullException.ThrowIfNull(replacement);
            var tree = spatialIndex;
            tree?.Delete(previous);
            tree?.Insert(replacement);
            dictionary[key] = replacement;
        }
    }

    public ICollection<ulong> Keys => dictionary.Keys;
    public ICollection<INode> Values => dictionary.Values;
    public int Count => dictionary.Count;
    public bool IsReadOnly => false;

    public void Add(ulong key, INode value)
    {
        var node = (Node)value;
        ArgumentNullException.ThrowIfNull(node);
        dictionary.Add(key, node);
        spatialIndex?.Insert(node);
    }

    public void Add(KeyValuePair<ulong, INode> item) => Add(item.Key, item.Value);

    public void Clear()
    {
        dictionary.Clear();
        spatialIndex?.Clear();
    }

    public bool Contains(KeyValuePair<ulong, INode> item) => dictionary.Contains(item);
    public bool ContainsKey(ulong key) => dictionary.ContainsKey(key);
    public bool ContainsValue(INode value) => dictionary.ContainsValue(value);
    public void CopyTo(KeyValuePair<ulong, INode>[] array, int arrayIndex) => ((IDictionary)dictionary).CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<ulong, INode>> GetEnumerator() => dictionary.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Remove(ulong key)
    {
        if (!dictionary.Remove(key, out var node)) return false;
        spatialIndex?.Delete((Node)node);
        return true;
    }

    public bool Remove(KeyValuePair<ulong, INode> item) => Remove(item.Key);
    public bool TryGetValue(ulong key, [MaybeNullWhen(false)] out INode value) => dictionary.TryGetValue(key, out value);

    public IReadOnlyList<Node> Within(double minX, double minZ, double maxX, double maxZ)
    {
        var tree = spatialIndex ?? BuildSpatialIndex();
        return tree.Search(new Envelope(minX, minZ, maxX, maxZ));
    }

    private RBush<Node> BuildSpatialIndex()
    {
        // Multiple readers may query an already published map for the first time together.
        lock (dictionary)
        {
            if (spatialIndex is { } existing) return existing;
            var tree = new RBush<Node>();
            tree.BulkLoad(dictionary.Values.Cast<Node>());
            spatialIndex = tree;
            return tree;
        }
    }
}
