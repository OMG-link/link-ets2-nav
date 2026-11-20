using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Map;

/// <summary>The topology uses the route node sequence validated at ingress.</summary>
public static class RouteStructureIdentity
{
    public static ulong[] Nodes(RouteSnapshot route)
    {
        if(route.Nodes.Count<2)return [];
        return route.Nodes.Select(n=>n.Uid).ToArray();
    }
}
