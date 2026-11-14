using System.Numerics;
namespace LinkEts2Nav.Map;

internal sealed record RoundaboutBoundaryPort(ulong NodeUid,Vector3 Position,IReadOnlyList<RoundaboutCurveKey> Curves);

/// <summary>Physical, directed boundary interfaces of an already recognized member region.</summary>
internal sealed record RoundaboutBoundary(IReadOnlyList<RoundaboutCurve> Curves,
    IReadOnlyList<RoundaboutBoundaryPort> Entries,IReadOnlyList<RoundaboutBoundaryPort> Exits)
{
    public static RoundaboutBoundary Build(IReadOnlyDictionary<RoundaboutCurveKey,RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring,IReadOnlyList<ulong> members)
    {
        var owners=members.ToHashSet();var ringItems=ring.Select(k=>k.ItemUid).ToHashSet();
        var local=graph.Values.Where(c=>c.PrefabUid is {} owner?owners.Contains(owner):ringItems.Contains(c.Key.ItemUid)).ToArray();
        var internalNodes=local.SelectMany(c=>new[]{(Node:c.EntryNode,c.Key.ItemUid),(Node:c.ExitNode,c.Key.ItemUid)})
            .Where(p=>p.Node!=0).GroupBy(p=>p.Node)
            .Where(g=>g.Select(p=>p.ItemUid).Distinct().Skip(1).Any()).Select(g=>g.Key).ToHashSet();
        RoundaboutBoundaryPort[] Ports(bool entry)=>local.Where(c=>(entry?c.EntryNode:c.ExitNode)!=0)
            .GroupBy(c=>entry?c.EntryNode:c.ExitNode).Where(g=>!internalNodes.Contains(g.Key))
            .OrderBy(g=>g.Key).Select(g=>
            {
                var lanes=g.OrderBy(c=>c.Key.ItemUid).ThenBy(c=>c.Key.CurveIndex).ToArray();
                return new RoundaboutBoundaryPort(g.Key,entry?lanes[0].Points[0]:lanes[0].Points[^1],lanes.Select(c=>c.Key).ToArray());
            }).ToArray();
        return new(local,Ports(true),Ports(false));
    }
}
