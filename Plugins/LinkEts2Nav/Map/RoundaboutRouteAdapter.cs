using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

/// <summary>Marks roundabout visits and creates their actions without replacing any route edge.</summary>
public static class RoundaboutRouteAdapter
{
    public static IReadOnlyList<RoundaboutRouteRange> Build(IReadOnlyList<DirectedRoad> route,
        IReadOnlyDictionary<ulong, RoundaboutStructure> annotations)
    {
        var result = new List<RoundaboutRouteRange>();
        var offsets=new double[route.Count+1];
        for(int i=0;i<route.Count;i++)offsets[i+1]=offsets[i]+RouteGeometry.Length(route[i]);
        for (int i = 0; i < route.Count; i++)
        {
            if (!TryOwner(route[i], out ulong uid) || !annotations.TryGetValue(uid, out var ring))
                continue;
            int last = i;
            while (last + 1 < route.Count && TryOwner(route[last + 1], out var nextUid)
                && annotations.TryGetValue(nextUid, out var nextRing) && nextRing.Id == ring.Id
                && !ring.ExitPorts.Any(p=>p.BoundaryNodeUid==route[last].EndNodeUid)) last++;
            var entry = ring.EntryPorts.SingleOrDefault(p => p.NodeUid == route[i].StartNodeUid);
            var exitMatches = ring.ExitPorts.Where(p => p.BoundaryNodeUid == route[last].EndNodeUid)
                .GroupBy(p => p.BoundaryNodeUid).ToArray();
            if(entry is null)
            {
                if(i==0)throw new NotSupportedException($"Roundabout {ring.Id}: route starts at {route[i].StartNodeUid:X16} without a retained entry interface; the entry-relative exit ordinal cannot be derived.");
                throw new InvalidDataException($"Roundabout {ring.Id}: route entry {route[i].StartNodeUid:X16} is not a declared entry interface.");
            }
            if(exitMatches.Length!=1||exitMatches[0].Count()!=1||!double.IsFinite(ring.RingLength)||ring.RingLength<=0)
                throw new InvalidDataException($"Roundabout {ring.Id}: invalid exit interface {route[last].EndNodeUid:X16} or reference length {ring.RingLength}.");
            // Bearings already follow the ring's native circulation direction.
            double Ahead(RoundaboutExitPort port)
            { return (port.Bearing - entry.Bearing + 2 * Math.PI) % (2 * Math.PI); }
            var ordered = ring.ExitPorts.GroupBy(p => p.BoundaryNodeUid)
                .Select(g => (g.Key, Along: g.Min(p => Ahead(p)))).OrderBy(p => p.Along).ToArray();
            bool ambiguous = ordered.Zip(ordered.Skip(1)).Any(p => Math.Abs(p.First.Along - p.Second.Along) < 1e-6);
            int ordinal = Array.FindIndex(ordered, p => p.Key == exitMatches[0].Key) + 1;
            if(ambiguous)throw new NotSupportedException($"Roundabout {ring.Id}: distinct exits have indistinguishable entry-relative bearings.");
            if(ordinal==0)throw new InvalidDataException($"Roundabout {ring.Id}: selected exit has no entry-relative ordinal.");
            var points = route.Skip(i).Take(last - i + 1).SelectMany(e => e.Points).ToArray();
            var starts=new List<int>();int pointOffset=0;
            foreach(var edge in route.Skip(i).Take(last-i+1))
            {
                if(pointOffset>0)starts.Add(pointOffset);
                starts.AddRange(edge.SegmentStarts.Select(n=>n+pointOffset));pointOffset+=edge.Points.Count;
            }
            double length=offsets[last+1]-offsets[i];
            var selectedPort=exitMatches[0].Single();
            double departure=NativeDeparture(selectedPort,route.Skip(i).Take(last-i+1))
                ??Project(selectedPort.DepartureCandidates,points,starts,0,length,true);
            var cues=new List<(int Number,double AlongMeters)>();double previous=0;
            for(int n=0;n<ordinal-1;n++)
            {
                var port=ring.ExitPorts.Where(p=>p.BoundaryNodeUid==ordered[n].Key)
                    .MinBy(p=>Ahead(p))!;
                double along=Project(port.DepartureCandidates,points,starts,previous,departure);
                cues.Add(new(n+1,along));previous=along;
            }
            cues.Add(new(ordinal,departure));
            var actions=BuildActions(ring.Id,i,route[i],route[last],offsets[i],offsets[last+1],ordinal,cues);
            result.Add(new(ring.Id,i,last+1,offsets[i],offsets[last+1],actions));
            i = last;
        }
        return result;
    }
    private static IReadOnlyList<NavigationManeuver> BuildActions(string ringId,int firstVisit,DirectedRoad entry,
        DirectedRoad exit,double start,double end,int exitNumber,IReadOnlyList<(int Number,double AlongMeters)> cues)
    {
        // The visit index distinguishes repeated traversals of the same physical
        // entry/exit without changing their identity or the original route.
        string sequence=$"roundabout:{ringId}:{entry.StartNodeUid:X16}:visit:{firstVisit}";
        string id=sequence+$":{exit.EndNodeUid:X16}";
        var actions=new List<NavigationManeuver>
        {
            new(id+":enter",ManeuverKind.EnterRoundabout,start,entry.Id,exit.Id)
            {EndDistanceMeters=end,JunctionId="roundabout:"+ringId,SpeechSequenceId=sequence,
                SpeechSequenceStartDistanceMeters=start,Instruction="进入环岛",
                NearInstruction="进入环岛，请从第"+NavigationPrompts.Number(exitNumber)+"出口驶出"}
        };
        foreach(var cue in cues)
        {
            bool selected=cue.Number==exitNumber;
            actions.Add(new(id+":exit:"+cue.Number,selected?ManeuverKind.ExitRoundabout:ManeuverKind.PassRoundaboutExit,
                start+cue.AlongMeters,entry.Id,exit.Id)
            {EndDistanceMeters=end,JunctionId="roundabout:"+ringId,ExecutionOnly=true,SpeechSequenceId=sequence,
                SpeechSequenceStartDistanceMeters=start,SpeechSequenceOrder=cue.Number,
                Instruction=selected?"驶出环岛":"经过第"+NavigationPrompts.Number(cue.Number)+"出口"});
        }
        return actions.ToArray();
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
        double offset=0,bestAlong=double.NaN,bestDistance=double.PositiveInfinity;
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
        if(!double.IsFinite(bestAlong))
            throw new InvalidDataException($"Roundabout departure has no projection onto a usable route segment in [{minimum}, {maximum}].");
        return bestAlong;
    }
    private static bool TryOwner(DirectedRoad road, out ulong uid)
    {
        var parts = road.Id.Split(':');
        return ulong.TryParse(parts.Length == 4 && parts[0] == "prefab" ? parts[1] : road.Id,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uid);
    }
}
