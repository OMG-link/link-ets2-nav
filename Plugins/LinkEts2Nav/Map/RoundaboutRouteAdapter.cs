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
            var starts=new List<int>();int pointOffset=0;
            foreach(var edge in route.Skip(i).Take(last-i+1))
            {
                if(pointOffset>0)starts.Add(pointOffset);
                starts.AddRange(edge.SegmentStarts.Select(n=>n+pointOffset));pointOffset+=edge.Points.Count;
            }
            double length=route.Skip(i).Take(last-i+1).Sum(RouteGeometry.Length);
            var selectedPort=exitMatches[0].Single();
            double departure=NativeDeparture(selectedPort,route.Skip(i).Take(last-i+1))
                ??Project(selectedPort.DepartureCandidates,points,starts,0,length,true);
            double connector=length-departure;
            var cues=new List<RoundaboutExitCue>();double previous=0;
            for(int n=0;n<ordinal-1;n++)
            {
                var port=ring.ExitPorts.Where(p=>p.BoundaryNodeUid==ordered[n].Key)
                    .MinBy(p=>Ahead(p))!;
                double along=Project(port.DepartureCandidates,points,starts,previous,departure);
                cues.Add(new(n+1,along));previous=along;
            }
            cues.Add(new(ordinal,departure));
            result.Add(new("roundabout:" + ring.Id, route[i].StartNodeUid, route[last].EndNodeUid, points)
            { SegmentStarts=starts, Roundabout = new(ring.Id, ordinal, connector){Exits=cues} });
            i = last;
        }
        return result;
    }
    // A target exit must use the actual selected native branch, not another lane
    // ring's spatially close departure near the entrance. Mileage follows the
    // selected polyline; metadata records indices, not a second route geometry.
    private static double? NativeDeparture(RoundaboutExitPort port,IEnumerable<DirectedRoad> route)
    {
        double offset=0;double? first=null;
        foreach(var edge in route)
        {
            if(TryOwner(edge,out ulong owner))
            {
                foreach(var span in edge.NativeCurveSpans)
                {
                    if(!port.DepartureBranches.Any(b=>b.Curve.ItemUid==owner&&b.Curve.CurveIndex==span.CurveIndex))continue;
                    double along=offset;
                    for(int p=1;p<=span.PointIndex;p++)
                        if(!edge.SegmentStarts.Contains(p))along+=Vector3.Distance(edge.Points[p-1],edge.Points[p]);
                    first=first is {} known?Math.Min(known,along):along;
                }
            }
            offset+=RouteGeometry.Length(edge);
        }
        return first;
    }
    // Locate each physical departure on the actual selected path, preserving exit order.
    private static double Project(IReadOnlyList<Vector3> candidates,IReadOnlyList<Vector3> path,IReadOnlyList<int> starts,
        double minimum,double maximum,bool preferLater=false)
    {
        double offset=0,bestAlong=minimum,bestDistance=double.PositiveInfinity;
        for(int i=1;i<path.Count;i++)
        {
            if(starts.Contains(i))continue;
            var a=path[i-1];var delta=path[i]-a;double length=delta.Length();
            if(length>0 && offset+length>=minimum && offset<=maximum)
            {
                double low=Math.Max(0,(minimum-offset)/length),high=Math.Min(1,(maximum-offset)/length);
                foreach(var point in candidates)
                {
                    float t=(float)Math.Clamp(Vector3.Dot(point-a,delta)/(length*length),low,high);
                    double distance=Vector3.DistanceSquared(point,a+t*delta),along=offset+t*length;
                    if(distance<bestDistance-1e-6||preferLater&&Math.Abs(distance-bestDistance)<=1e-6&&along>bestAlong)
                    {bestDistance=distance;bestAlong=along;}
                }
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
