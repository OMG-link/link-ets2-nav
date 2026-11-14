using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

/// <summary>Read-only traversal of already loaded items; compound children already have world-space nodes.</summary>
public static class LoadedMapItems
{
    /// <summary>Top-level items win duplicate UIDs. Every physical item is returned at most once.</summary>
    public static IEnumerable<MapItem> Enumerate(IEnumerable<MapItem> source)
    {
        var roots=source.ToArray();var seen=new HashSet<ulong>();var pending=new Stack<Compound>();
        foreach(var item in roots)
            if(seen.Add(item.Uid))
            {
                yield return item;
                if(item is Compound compound)pending.Push(compound);
            }
        while(pending.TryPop(out var parent))
            foreach(var item in parent.MapItems.Values)
                if(seen.Add(item.Uid))
                {
                    yield return item;
                    if(item is Compound compound)pending.Push(compound);
                }
    }

    /// <summary>
    /// Prunes a newly deserialized supplement container before publication. Never call on host-owned items.
    /// Child nodes retain their original owner, position, rotation and item references.
    /// </summary>
    public static bool RetainSupplementSigns(Compound compound)
    {
        var visited=new HashSet<Compound>(ReferenceEqualityComparer.Instance);
        bool Trim(Compound current)
        {
            if(!visited.Add(current))return false;
            foreach(var item in current.MapItems.Values.ToArray())
            {
                bool keep=item is Sign||item is Compound nested&&Trim(nested);
                if(!keep)current.MapItems.Remove(item.Uid);
            }
            var nodes=current.MapItems.Values.OfType<SingleNodeItem>().Select(i=>i.Node.Uid).ToHashSet();
            foreach(ulong uid in current.Nodes.Keys.Where(uid=>!nodes.Contains(uid)).ToArray())current.Nodes.Remove(uid);
            return current.MapItems.Count>0;
        }
        return Trim(compound);
    }
}
