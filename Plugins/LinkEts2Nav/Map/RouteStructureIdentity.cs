using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Map;

/// <summary>The ingress node sequence is topology; only strictly decreasing remaining distances define forward order.</summary>
public static class RouteStructureIdentity
{
    public static bool HasForwardOrder(RouteSnapshot route)=>route.Nodes.Zip(route.Nodes.Skip(1))
        .All(p=>p.First.DistanceToEndMeters>p.Second.DistanceToEndMeters);

    public static ulong[] Nodes(RouteSnapshot route)
    {
        if(route.Nodes.Count<2||!HasForwardOrder(route))return [];
        return route.Nodes.Select(n=>n.Uid).ToArray();
    }
}
