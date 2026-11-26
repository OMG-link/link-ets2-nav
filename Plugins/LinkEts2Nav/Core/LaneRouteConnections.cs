using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>The shared native and permitted parallel-lane transfer policy.</summary>
public static class LaneRouteConnections
{
    internal const double NativeSeamMeters = .5;

    public static (bool Possible,bool Same,bool Allowed) Between(PrefabLanePath a,PrefabLanePath b,bool adjacent)
    {
        double gap=Vector3.Distance(a.Points[^1],b.Points[0]);
        if(adjacent&&gap<=NativeSeamMeters)return(true,true,false);
        bool same=a.ExitLaneCount==b.EntryLaneCount&&a.ExitOrder==b.EntryOrder;
        if(!adjacent)return(true,same,!same&&a.ExitLaneCount==b.EntryLaneCount);
        var before=new LanePolyline(a.Points);var after=new LanePolyline(b.Points);
        var heading=before.Heading(before.Length);var delta=b.Points[0]-a.Points[^1];
        bool parallel=a.ExitLaneCount==b.EntryLaneCount&&Math.Abs(delta.Y)<=.5
            &&Math.Abs(Vector3.Dot(delta,heading))<=1&&gap<=6*Math.Max(1,Math.Abs(a.ExitOrder-b.EntryOrder))
            &&Vector3.Dot(heading,after.Heading(0))>=Math.Cos(Math.PI/180);
        return(parallel,false,parallel);
    }
}
