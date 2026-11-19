using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Map;

/// <summary>The directed node sequence is topology; remaining distance, ETA and refresh timestamps are not.</summary>
public static class RouteStructureIdentity
{
    public static ulong[] Nodes(RouteSnapshot route)=>SourceNodeIndices(route).Select(i=>route.Nodes[i].Uid).ToArray();

    /// <summary>FullRoute ingress-node indices, ordered in the direction of travel.</summary>
    public static int[] SourceNodeIndices(RouteSnapshot route)
    {
        var indices=Enumerable.Range(0,route.Nodes.Count).ToArray();
        if(indices.Length<2)return [];
        bool descending=indices.Zip(indices.Skip(1)).All(p=>route.Nodes[p.First].DistanceToEndMeters>route.Nodes[p.Second].DistanceToEndMeters);
        bool ascending=indices.Zip(indices.Skip(1)).All(p=>route.Nodes[p.First].DistanceToEndMeters<route.Nodes[p.Second].DistanceToEndMeters);
        if(!descending&&!ascending)return [];
        if(!descending)Array.Reverse(indices);
        return indices;
    }
}
