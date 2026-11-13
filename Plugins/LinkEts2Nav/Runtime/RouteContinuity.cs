namespace LinkEts2Nav.Runtime;

/// <summary>Distinguishes normal SDK prefix trimming/tail extension from a replacement route.</summary>
public static class RouteContinuity
{
    public static bool IsForwardExtension(RouteSnapshot oldRoute,RouteSnapshot newRoute)
    {
        var old=oldRoute.Nodes.Select(n=>n.Uid).ToArray();var current=newRoute.Nodes.Select(n=>n.Uid).ToArray();
        if(old.Length==0||current.Length==0)return old.Length==current.Length;
        for(int offset=0;offset<old.Length;offset++)
        {
            if(old[offset]!=current[0])continue;
            int count=Math.Min(old.Length-offset,current.Length);
            if(Enumerable.Range(0,count).All(i=>old[offset+i]==current[i]))return true;
        }
        return false;
    }
}
