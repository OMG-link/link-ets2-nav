using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Runtime;

/// <summary>Only a complete SDK route whose final node is the verified world's path end can supply a destination cue.</summary>
public static class NavigationDestination
{
    public static NavigationManeuver? Find(RouteSnapshot sdkRoute,IReadOnlyList<DirectedRoad> path,
        double vehicleAlong,double confirmedUntil,bool unresolvedJoin)
    {
        if(sdkRoute.Availability!=RouteAvailability.Available||sdkRoute.MayBeTruncated
            ||sdkRoute.Nodes.Count<2||path.Count==0||unresolvedJoin||!double.IsFinite(vehicleAlong)||vehicleAlong<0)
            return null;
        var terminal=sdkRoute.Nodes.MinBy(n=>n.DistanceToEndMeters)!;
        // SDK distances are scaled driving distances, used only to recognize a
        // terminal record. Never substitute them for meters on the world path.
        if(terminal.Uid==0||!float.IsFinite(terminal.DistanceToEndMeters)||terminal.DistanceToEndMeters>.01f
            ||path[^1].EndNodeUid!=terminal.Uid)return null;
        double length=path.Sum(RouteGeometry.Length);
        if(!double.IsFinite(length)||length<vehicleAlong||!double.IsFinite(confirmedUntil)||confirmedUntil<length-.01)
            return null;
        double remaining=length-vehicleAlong;
        return new("destination:"+terminal.Uid.ToString("X16"),ManeuverKind.Destination,remaining,path[^1].Id,path[^1].Id)
            {EndDistanceMeters=remaining};
    }
}
