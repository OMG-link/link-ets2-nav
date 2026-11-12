using System.Numerics;
namespace LinkEts2Nav.Core;

/// <summary>
/// Follows an added side entrance's native lane lineage along the official road
/// route. Planned lateral changes are deliberately not used as native continuity.
/// </summary>
public sealed class AccelerationLaneApproach
{
    private sealed record Segment(string Id,string Origin,string EndEdge,double EndOffset,double Length,
        bool Left,string Junction,IReadOnlySet<string> Corridor);
    private readonly Dictionary<string,Segment> segments=new(StringComparer.Ordinal);
    public void Reset()=>segments.Clear();

    public IReadOnlyList<NavigationManeuver> Find(IReadOnlyList<DirectedRoad> route,
        IReadOnlyList<DirectedRoad> graph,double vehicleAlong)
    {
        if(route.Zip(route.Skip(1)).Any(p=>p.First.EndNodeUid!=p.Second.StartNodeUid))return [];
        double[] offsets=new double[route.Count+1];
        for(int i=0;i<route.Count;i++)offsets[i+1]=offsets[i]+Length(route[i]);
        var current=Enumerable.Range(0,route.Count).FirstOrDefault(i=>offsets[i+1]>=vehicleAlong,-1);
        var active=route.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var key in segments.Keys.ToArray())
        {
            var s=segments[key];
            if(!active.Contains(s.EndEdge)||!active.Contains(s.Origin)
                &&(current<0||!s.Corridor.Contains(route[current].Id)))segments.Remove(key);
        }
        for(int i=0;i<route.Count;i++)
        {
            var edge=route[i];
            if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal)||edge.Roundabout is not null||edge.Junction?.IsJunction==true)continue;
            var decision=RoadJoinClassifier.Classify(edge);
            if(decision.Role!=RoadJoinRole.Entering||decision.MainEntry is not {} mainEntry
                ||RoadJoinTopology.IndependentEntries(edge.LaneConnections,edge.EndNodeUid).Count==0)continue;
            string prefab="prefab:"+edge.Id.Split(':')[1]+":";
            var main=graph.Where(e=>e.Id.StartsWith(prefab,StringComparison.Ordinal)&&e.StartNodeUid==mainEntry&&e.EndNodeUid==edge.EndNodeUid)
                .SelectMany(e=>e.LanePaths).Select(p=>p.ExitOrder).ToHashSet();
            var side=edge.LanePaths.Select(p=>p.ExitOrder).ToHashSet();
            if(main.Count==0||side.Count==0||main.Overlaps(side))continue;
            var segment=Follow(i,side,main);
            if(segment is not null)segments[segment.Id]=segment;
        }
        var result=new List<NavigationManeuver>();
        foreach(var s in segments.Values)
        {
            int endIndex=Enumerable.Range(0,route.Count).FirstOrDefault(i=>route[i].Id==s.EndEdge,-1);
            if(endIndex<0)continue;
            double end=offsets[endIndex]+s.EndOffset-vehicleAlong;
            if(end<0)continue;
            result.Add(new(s.Id,s.Left?ManeuverKind.MergeLeft:ManeuverKind.MergeRight,new LaneActionInterval(end-s.Length,end).AtFraction(.1),s.Origin,s.EndEdge)
                {EndDistanceMeters=end,JunctionId=s.Junction,HasSeparateApproachRoad=false,ApproachInterval=new(end-s.Length,end)});
        }
        return result.OrderBy(m=>m.DistanceMeters).ToArray();

        Segment? Follow(int origin,HashSet<int> side,HashSet<int> main)
        {
            double start=offsets[origin+1];
            int laneCount=route[origin].LanePaths[0].ExitLaneCount;
            var corridor=new HashSet<string>(StringComparer.Ordinal){route[origin].Id};
            for(int j=origin+1;j<route.Count;j++)
            {
                var edge=route[j];corridor.Add(edge.Id);
                // Keep the existing ramp/lane-reduction distinction. Longer
                // parallel reductions retain their separate environmental warning.
                if(offsets[j]-start>500||edge.Roundabout is not null||edge.Junction?.IsJunction==true)return null;
                if(!edge.Id.StartsWith("prefab:",StringComparison.Ordinal))continue;
                if(edge.LanePaths.Any(p=>p.EntryLaneCount!=laneCount))return null;
                var mainPaths=edge.LanePaths.Where(p=>main.Contains(p.EntryOrder)).ToArray();
                if(mainPaths.Length==0)return null; // The route left the original main road.
                var sidePaths=edge.LanePaths.Where(p=>side.Contains(p.EntryOrder)).ToArray();
                bool? left=side.Min()>main.Max()?true:side.Max()<main.Min()?false:null;
                if(left is null)return null;
                double? end=null;
                if(sidePaths.Length==0)
                {
                    // The lane remains physical, but leads to another road. To
                    // satisfy the official exit, leaving this side lane is mandatory.
                    string prefab="prefab:"+edge.Id.Split(':')[1]+":";
                    bool leaves=graph.Where(e=>e.Id.StartsWith(prefab,StringComparison.Ordinal)
                        &&e.StartNodeUid==edge.StartNodeUid&&e.EndNodeUid!=edge.EndNodeUid)
                        .SelectMany(e=>e.LanePaths).Any(p=>side.Contains(p.EntryOrder));
                    if(leaves)end=offsets[j];
                }
                else
                {
                    var mainOutputs=mainPaths.Select(p=>p.ExitLane).ToHashSet();
                    var independent=sidePaths.Where(p=>!mainOutputs.Contains(p.ExitLane)).ToArray();
                    if(independent.Length>0)
                    {
                        side=independent.Select(p=>p.ExitOrder).ToHashSet();
                        main=mainPaths.Select(p=>p.ExitOrder).ToHashSet();
                        laneCount=independent[0].ExitLaneCount;
                        continue;
                    }
                    // All native continuations of this side group join the main
                    // lanes. Require a real common suffix and agreeing native lamps.
                    var positions=new List<double>();
                    foreach(var s in sidePaths)foreach(var m in mainPaths.Where(p=>p.ExitLane==s.ExitLane))
                    {
                        int common=0;
                        while(common<Math.Min(s.CurveIndices.Count,m.CurveIndices.Count)
                            &&s.CurveIndices[^(common+1)]==m.CurveIndices[^(common+1)])common++;
                        if(common==0||common>=Math.Min(s.CurveIndices.Count,m.CurveIndices.Count)
                            ||s.CurveStartPointIndices.Count!=s.CurveIndices.Count)continue;
                        var signal=MergeBlinkerDirection.Classify(s,m,common);
                        if(signal.FirstEnds!=true||signal.MoveLeft!=left)return null;
                        var position=s.Points[s.CurveStartPointIndices[^common]];
                        var projection=new LanePolyline(edge.Points).Project(position);
                        if(projection.Error<=15)positions.Add(offsets[j]+projection.Along);
                    }
                    if(positions.Count>0)end=positions.Min();
                }
                if(end is not {} finish||finish<=start||finish-start>500)return null;
                string id="acceleration:"+route[origin].Id+":"+edge.Id;
                string junction="lane-merge:"+edge.Id.Split(':')[1];
                return new(id,route[origin].Id,edge.Id,finish-offsets[j],finish-start,left.Value,junction,corridor);
            }
            return null;
        }
    }
    private static double Length(DirectedRoad road)=>new LanePolyline(road.Points).Length;
}
