using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Collapses each selected ring passage, preserving its actual route geometry and length.</summary>
public static class RoundaboutRouteAdapter
{
    public static IReadOnlyList<DirectedRoad> Apply(IReadOnlyList<DirectedRoad> route,
        IReadOnlyDictionary<ulong, RoundaboutStructure> annotations)
    {
        var result = new List<DirectedRoad>();
        for (int i = 0; i < route.Count; i++)
        {
            if (!TryOwner(route[i], out ulong uid) || !annotations.TryGetValue(uid, out var ring))
            { result.Add(route[i]); continue; }
            int last = i;
            while (last + 1 < route.Count && TryOwner(route[last + 1], out var nextUid)
                && annotations.TryGetValue(nextUid, out var nextRing) && nextRing.Id == ring.Id) last++;
            var entry = ring.EntryPorts.SingleOrDefault(p => p.NodeUid == route[i].StartNodeUid);
            var exitMatches = ring.ExitPorts.Where(p => p.BoundaryNodeUid == route[last].EndNodeUid)
                .GroupBy(p => p.BoundaryNodeUid).ToArray();
            if (entry is null || exitMatches.Length != 1 || ring.RingLength <= 0)
            { result.AddRange(route.Skip(i).Take(last - i + 1)); i = last; continue; }
            // Bearings already follow the ring's native circulation direction.
            double Ahead(RoundaboutExitPort port)
            { return (port.Bearing - entry.Bearing + 2 * Math.PI) % (2 * Math.PI); }
            var ordered = ring.ExitPorts.GroupBy(p => p.BoundaryNodeUid)
                .Select(g => (g.Key, Along: g.Min(p => Ahead(p)))).OrderBy(p => p.Along).ToArray();
            bool ambiguous = ordered.Zip(ordered.Skip(1)).Any(p => Math.Abs(p.First.Along - p.Second.Along) < 1e-6);
            int ordinal = Array.FindIndex(ordered, p => p.Key == exitMatches[0].Key) + 1;
            if (ambiguous || ordinal == 0)
            { result.AddRange(route.Skip(i).Take(last - i + 1)); i = last; continue; }
            var points = route.Skip(i).Take(last - i + 1).SelectMany(e => e.Points).ToArray();
            double length=points.Zip(points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second));
            double connector=exitMatches[0].Min(p=>p.DepartureToBoundaryMeters);
            double departure=Math.Max(0,length-connector);
            var cues=new List<RoundaboutExitCue>();double previous=0;
            for(int n=0;n<ordinal-1;n++)
            {
                var port=ring.ExitPorts.Where(p=>p.BoundaryNodeUid==ordered[n].Key)
                    .MinBy(p=>Ahead(p))!;
                double along=Project(port.DeparturePosition,points,previous,departure);
                cues.Add(new(n+1,along));previous=along;
            }
            cues.Add(new(ordinal,departure));
            result.Add(new("roundabout:" + ring.Id, route[i].StartNodeUid, route[last].EndNodeUid, points)
            { Roundabout = new(ring.Id, ordinal, connector){Exits=cues} });
            i = last;
        }
        return result;
    }
    // Locate each physical departure on the actual selected path, preserving exit order.
    private static double Project(Vector3 point,IReadOnlyList<Vector3> path,double minimum,double maximum)
    {
        double offset=0,bestAlong=minimum,bestDistance=double.PositiveInfinity;
        for(int i=1;i<path.Count;i++)
        {
            var a=path[i-1];var delta=path[i]-a;double length=delta.Length();
            if(length>0 && offset+length>=minimum && offset<=maximum)
            {
                double low=Math.Max(0,(minimum-offset)/length),high=Math.Min(1,(maximum-offset)/length);
                float t=(float)Math.Clamp(Vector3.Dot(point-a,delta)/(length*length),low,high);
                double distance=Vector3.DistanceSquared(point,a+t*delta);
                if(distance<bestDistance){bestDistance=distance;bestAlong=offset+t*length;}
            }
            offset+=length;
        }
        return bestAlong;
    }
    private static bool TryOwner(DirectedRoad road, out ulong uid)
    {
        var parts = road.Id.Split(':');
        return ulong.TryParse(parts.Length == 4 && parts[0] == "prefab" ? parts[1] : road.Id,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uid);
    }
}
