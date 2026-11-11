using System.Numerics;
namespace LinkEts2Nav.Core;

public enum LaneMergeKind { Ramp, LaneReduction, Uncertain }
public sealed record LaneMergeObservation(string Id,ulong PrefabUid,ulong EndingEntryNode,int EndingEntryLane,
    ulong MainEntryNode,int MainEntryLane,Vector3 Position,bool MoveLeft,int RecommendedLaneCount,
    double? ParallelDistanceMeters,LaneMergeKind Kind,double DistanceMeters,string Reason);

/// <summary>Topology proves the merge; curvature continuity identifies the continuing lane; paired geometry measures its history.</summary>
public sealed class LaneMergeDetector
{
    private IReadOnlyList<DirectedRoad>? indexed;
    private IReadOnlyList<Model> models=[];
    private readonly HashSet<string> approaching=new(StringComparer.Ordinal);
    private sealed record Model(PrefabLanePath Ending,PrefabLanePath Main,Vector3 Position,bool Left,int Count,
        double? Parallel,LaneMergeKind Kind,string Reason);
    public IReadOnlyList<LaneMergeObservation> Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        LaneNavigationPlan plan,double vehicleAlong)
    {
        if(!ReferenceEquals(indexed,graph)){indexed=graph;models=Build(graph);}
        var result=new List<LaneMergeObservation>();double offset=0;
        foreach(var edge in route)
        {
            var selected=plan.Sections.FirstOrDefault(s=>s.EdgeId==edge.Id)?.Path;
            foreach(var m in models.Where(m=>edge.LanePaths.Any(p=>p.PrefabUid==m.Ending.PrefabUid
                &&p.EntryNodeUid==m.Ending.EntryNodeUid&&p.ExitNodeUid==m.Ending.ExitNodeUid)))
            {
                bool usesEnding=selected is not null&&selected.EntryNodeUid==m.Ending.EntryNodeUid
                    &&selected.EntryLane==m.Ending.EntryLane&&selected.ExitNodeUid==m.Ending.ExitNodeUid;
                string id=$"lane-merge:{m.Ending.PrefabUid:X16}:{m.Ending.CurveIndices[^1]}:{m.Ending.EntryNodeUid:X16}:{m.Ending.EntryLane}";
                if(m.Kind!=LaneMergeKind.LaneReduction&&!usesEnding&&!approaching.Contains(id))continue;
                var projected=new LanePolyline(edge.Points).Project(m.Position);
                double distance=offset+projected.Along-vehicleAlong;
                if(distance<0){approaching.Remove(id);continue;}
                if(projected.Error>15)continue;
                if(usesEnding)approaching.Add(id);
                result.Add(new(id,m.Ending.PrefabUid,m.Ending.EntryNodeUid,m.Ending.EntryLane,m.Main.EntryNodeUid,m.Main.EntryLane,
                    m.Position,m.Left,m.Count,m.Parallel,m.Kind,distance,m.Reason));
            }
            offset+=new LanePolyline(edge.Points).Length;
        }
        approaching.IntersectWith(result.Select(m=>m.Id));
        return result.DistinctBy(m=>m.Id).OrderBy(m=>m.DistanceMeters).ToArray();
    }
    public void Reset()=>approaching.Clear();

    private static IReadOnlyList<Model> Build(IReadOnlyList<DirectedRoad> graph)
    {
        var lanes=graph.SelectMany(e=>e.LanePaths).DistinctBy(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane)).ToArray();
        var roads=graph.Where(e=>!e.Id.StartsWith("prefab:",StringComparison.Ordinal)).GroupBy(e=>e.EndNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        var laneEnds=lanes.GroupBy(p=>p.ExitNodeUid).ToDictionary(g=>g.Key,g=>g.ToArray());
        var result=new List<Model>();
        foreach(var group in lanes.GroupBy(p=>(p.PrefabUid,p.ExitNodeUid,p.ExitLane)))
        {
            var paths=group.ToArray();
            for(int a=0;a<paths.Length;a++)for(int b=a+1;b<paths.Length;b++)
            {
                var x=paths[a];var y=paths[b];int common=0;
                while(common<Math.Min(x.CurveIndices.Count,y.CurveIndices.Count)&&x.CurveIndices[^(common+1)]==y.CurveIndices[^(common+1)])common++;
                if(common==0||common==Math.Min(x.CurveIndices.Count,y.CurveIndices.Count))continue;
                int xi=x.Points.Count-1,yi=y.Points.Count-1;
                while(xi>0&&yi>0&&Horizontal(x.Points[xi-1]-y.Points[yi-1])<.0001){xi--;yi--;}
                if(x.CurveStartPointIndices.Count==x.CurveIndices.Count)xi=x.CurveStartPointIndices[^common];
                if(y.CurveStartPointIndices.Count==y.CurveIndices.Count)yi=y.CurveStartPointIndices[^common];
                var xp=new LanePolyline(x.Points);var yp=new LanePolyline(y.Points);
                double xa=xp.Along[xi],ya=yp.Along[yi];if(xa<6||ya<6||xp.Length-xa<1)continue;
                // Measure only the shared suffix. Short suffixes must not borrow
                // curvature from either competing approach before the merge.
                var shared=new LanePolyline(x.Points.Skip(xi));
                double outgoing=shared.Curvature(Math.Min(3,shared.Length*.5));
                double xs=Continuity(xp,xa,outgoing),ys=Continuity(yp,ya,outgoing);
                if(Math.Abs(xs-ys)<.015||Math.Max(xs,ys)<1.5*Math.Max(.0001,Math.Min(xs,ys)))continue;
                bool xEnds=xs>ys;
                var ending=xEnds?x:y;var main=xEnds?y:x;int ei=xEnds?xi:yi,mi=xEnds?yi:xi;
                var endTrace=Trace(ending,ei,roads,laneEnds);var mainTrace=Trace(main,mi,roads,laneEnds);
                var measured=MeasureParallel(endTrace,mainTrace);
                if(!measured.Observed)continue;
                double? parallel=measured.Distance;
                var kind=parallel is null?LaneMergeKind.Uncertain:parallel<500?LaneMergeKind.Ramp:LaneMergeKind.LaneReduction;
                var endLine=new LanePolyline(ending.Points);var mainLine=new LanePolyline(main.Points);
                var endPosition=endLine.At(Math.Max(0,endLine.Along[ei]-Math.Min(30,endLine.Along[ei])));
                var mainNear=mainLine.Project(endPosition);var heading=mainLine.Heading(mainNear.Along);
                bool left=Vector3.Dot(endPosition-mainLine.At(mainNear.Along),new(-heading.Z,0,heading.X))>0;
                int count=lanes.Where(p=>p.PrefabUid==ending.PrefabUid&&p.EntryNodeUid==ending.EntryNodeUid&&p.ExitNodeUid==ending.ExitNodeUid)
                    .Where(p=>left?p.EntryOrder<ending.EntryOrder:p.EntryOrder>ending.EntryOrder).Select(p=>p.EntryLane).Distinct().Count();
                count=Math.Max(1,count);
                result.Add(new(ending,main,ending.Points[ei],left,count,parallel,kind,
                    parallel is null?"已确认侧向合并，但上游平行段超出已加载或唯一连接范围":$"几何连续性区分延续车道；合并前平行接近区间约 {parallel:F0} m"));
            }
        }
        return result.DistinctBy(m=>(m.Ending.PrefabUid,m.Ending.EntryNodeUid,m.Ending.EntryLane,m.Ending.ExitNodeUid,m.Ending.ExitLane)).ToArray();
    }
    private static double Continuity(LanePolyline path,double merge,double outgoing)
    {
        double cost=0;
        for(double d=2;d<=Math.Min(30,merge-2);d+=2)cost+=2*Math.Abs(path.Curvature(merge-d)-outgoing);
        return cost;
    }
    private static LanePolyline Trace(PrefabLanePath current,int endIndex,IReadOnlyDictionary<ulong,DirectedRoad[]> roadEnds,IReadOnlyDictionary<ulong,PrefabLanePath[]> laneEnds)
    {
        var points=current.Points.Take(endIndex+1).Reverse().ToList();double length=new LanePolyline(points).Length;
        var visited=new HashSet<(ulong,ulong,int,ulong,int)>();
        var visitedRoads=new HashSet<(string,ulong,ulong)>();
        void Add(IEnumerable<Vector3> source)
        {
            foreach(var p in source){if(points.Count>0){double gap=Vector3.Distance(points[^1],p);if(gap<.001)continue;length+=gap;}points.Add(p);if(length>=550)break;}
        }
        while(length<550&&visited.Add((current.PrefabUid,current.EntryNodeUid,current.EntryLane,current.ExitNodeUid,current.ExitLane)))
        {
            var currentLine=new LanePolyline(current.Points);var heading=currentLine.Heading(0);
            ulong boundary=current.EntryNodeUid;Vector3 expected=current.Points[0];bool blocked=false;
            // Several ordinary road items can connect directly without a prefab between them.
            while(length<550)
            {
                var roads=(roadEnds.GetValueOrDefault(boundary)??[]).Where(e=>Vector3.Dot(new LanePolyline(e.Points).Heading(new LanePolyline(e.Points).Length),heading)>.7).ToArray();
                if(roads.Length==0)break;
                if(roads.Length>1){blocked=true;break;}
                var road=roads[0];
                if(!visitedRoads.Add((road.Id,road.StartNodeUid,road.EndNodeUid))){blocked=true;break;}
                var line=new LanePolyline(road.Points);var h=line.Heading(line.Length);var right=new Vector3(-h.Z,0,h.X);
                float lateral=Vector3.Dot(expected-road.Points[^1],right);
                var shifted=road.Points.Select((p,i)=>{var t=line.Heading(line.Along[i]);return p+new Vector3(-t.Z,0,t.X)*lateral;}).ToArray();
                if(Vector3.Distance(shifted[^1],expected)>1){blocked=true;break;}
                Add(shifted.Reverse());boundary=road.StartNodeUid;expected=shifted[0];heading=line.Heading(0);
            }
            if(blocked||length>=550)break;
            var prior=(laneEnds.GetValueOrDefault(boundary)??[]).Where(p=>p.ExitOrder==current.EntryOrder&&p.ExitLaneCount==current.EntryLaneCount
                &&Vector3.Distance(p.Points[^1],expected)<1).ToArray();
            if(prior.Length>1)
            {
                // Side roads can join this exit lane. Prefer the unique upstream
                // lane preserving the corridor's lane count, order and direction.
                prior=prior.Where(p=>p.EntryLaneCount==current.EntryLaneCount&&p.EntryOrder==current.EntryOrder
                    &&Vector3.Dot(new LanePolyline(p.Points).Heading(0),heading)>.7).ToArray();
            }
            if(prior.Length!=1)break;
            current=prior[0];Add(current.Points.Reverse());
        }
        return new(points);
    }
    private static (bool Observed,double? Distance) MeasureParallel(LanePolyline ending,LanePolyline main)
    {
        bool observed=false;int stable=0;
        for(double d=2;d<=Math.Min(550,ending.Length);d+=2)
        {
            var p=ending.At(d);var projection=main.Project(p,Math.Max(0,d-Math.Max(15,d*.1)),Math.Min(main.Length,d+Math.Max(15,d*.1)));
            bool covered=projection.Along<main.Length-1;
            bool parallel=covered&&projection.Error is >=2 and <=6.5&&Math.Abs(p.Y-main.At(projection.Along).Y)<2
                &&Vector3.Dot(ending.Heading(d),main.Heading(projection.Along))>=Math.Cos(Math.PI/180);
            if(parallel){stable++;if(stable>=3)observed=true;}
            else if(observed&&covered)return(true,Math.Max(0,d-2));
            else stable=0;
            if(observed&&d>=500&&parallel)return(true,500);
        }
        return(observed,null);
    }
    private static float Horizontal(Vector3 p)=>new Vector2(p.X,p.Z).Length();
}

/// <summary>Arc-length sampling and local curvature; duplicate PPD boundary samples are harmless.</summary>
internal sealed class LanePolyline
{
    public IReadOnlyList<Vector3> Points { get; }
    public double[] Along { get; }
    public double Length=>Along.Length==0?0:Along[^1];
    public LanePolyline(IEnumerable<Vector3> points)
    {
        Points=points.ToArray();Along=new double[Points.Count];
        for(int i=1;i<Points.Count;i++)Along[i]=Along[i-1]+Vector3.Distance(Points[i-1],Points[i]);
    }
    public Vector3 At(double along)
    {
        if(Points.Count==0)return default;if(along<=0)return Points[0];if(along>=Length)return Points[^1];
        int i=Array.BinarySearch(Along,along);if(i>=0)return Points[i];i=~i;
        double length=Along[i]-Along[i-1];return Vector3.Lerp(Points[i-1],Points[i],length>0?(float)((along-Along[i-1])/length):0);
    }
    public Vector3 Heading(double along)
    {
        var delta=At(Math.Min(Length,along+1))-At(Math.Max(0,along-1));delta.Y=0;
        return delta.LengthSquared()>.000001?Vector3.Normalize(delta):Vector3.Zero;
    }
    public double Curvature(double along)
    {
        double a=Math.Max(0,along-2),b=Math.Min(Length,along+2);if(b-a<.1)return 0;
        var x=Heading(a);var y=Heading(b);return Math.Atan2(x.X*y.Z-x.Z*y.X,Vector3.Dot(x,y))/(b-a);
    }
    public (double Along,double Error) Project(Vector3 point,double minimum=0,double maximum=double.PositiveInfinity)
    {
        (double Along,double Error) best=(0,double.PositiveInfinity);
        for(int i=1;i<Points.Count;i++)
        {
            if(Along[i]<minimum||Along[i-1]>maximum)continue;
            var d=Points[i]-Points[i-1];float len=d.X*d.X+d.Z*d.Z;if(len<.000001)continue;
            float t=Math.Clamp(((point.X-Points[i-1].X)*d.X+(point.Z-Points[i-1].Z)*d.Z)/len,0,1);
            double at=Along[i-1]+t*(Along[i]-Along[i-1]);at=Math.Clamp(at,minimum,Math.Min(maximum,Length));
            var p=At(at);double error=new Vector2(point.X-p.X,point.Z-p.Z).Length();
            if(error<best.Error)best=(at,error);
        }
        return best;
    }
}
