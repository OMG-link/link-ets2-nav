using System.Numerics;
namespace LinkEts2Nav.Core;

public enum LaneMergeKind { Ramp, LaneReduction, Uncertain }
public sealed record LaneMergeObservation(string Id,ulong PrefabUid,ulong? EndingEntryNode,int? EndingEntryLane,
    ulong? MainEntryNode,int? MainEntryLane,Vector3 Position,bool? MoveLeft,int RecommendedLaneCount,
    double? ParallelDistanceMeters,LaneMergeKind Kind,double DistanceMeters,string Reason)
{
    public double? EndDistanceMeters { get; init; }
    public LaneActionInterval? ApproachInterval { get; init; }
    public LaneMergeOriginDecision? Origin { get; init; }
    public bool RouteConstrained { get; init; }
    public bool EntranceBased { get; init; }
    public string? CompletionJunctionId { get; init; }
    public string? JunctionId { get; init; }
    public int? ApproachLaneCount { get; init; }
    public IReadOnlyList<int> SurvivingEntryOrders {get;init;}=[];
    public bool AllIncomingLanesDie {get;init;}
}

/// <summary>Native continuity proves a reduction; the shared source window classifies its history.</summary>
public sealed class LaneMergeDetector
{
    private IReadOnlyList<DirectedRoad>? indexed;
    private IReadOnlyList<Model> models=[];
    private readonly HashSet<string> approaching=new(StringComparer.Ordinal);
    private sealed record Model(PrefabLanePath Ending,PrefabLanePath Main,Vector3 Position,bool? Left,int Count,
        double? Parallel,LaneMergeKind Kind,string Reason,LaneMergeOriginDecision Origin,
        IReadOnlyList<int> SurvivingOrders,bool AllIncomingDie);
    public IReadOnlyList<LaneMergeObservation> Find(IReadOnlyList<DirectedRoad> route,IReadOnlyList<DirectedRoad> graph,
        LaneNavigationPlan plan,double vehicleAlong,Func<ulong,LinkEts2Nav.Map.MapQueryIssue>? missingConnection=null)
    {
        if(!ReferenceEquals(indexed,graph)){indexed=graph;models=Build(graph,missingConnection);}
        var result=new List<LaneMergeObservation>();double offset=0;
        foreach(var edge in route)
        {
            var selected=plan.Sections.FirstOrDefault(s=>s.EdgeId==edge.Id)?.Path;
            foreach(var m in models.Where(m=>edge.LanePaths.Any(p=>p.PrefabUid==m.Ending.PrefabUid
                &&(p.EntryNodeUid==m.Ending.EntryNodeUid||p.EntryNodeUid==m.Main.EntryNodeUid)&&p.ExitNodeUid==m.Ending.ExitNodeUid)))
            {
                if(m.Kind==LaneMergeKind.LaneReduction&&(selected is null
                    ||selected.EntryNodeUid!=m.Origin.ApplicableEntryNode
                    ||!m.Origin.ApplicableEntryLanes.Contains(selected.EntryLane)))continue;
                bool usesEnding=selected is not null&&selected.EntryNodeUid==m.Ending.EntryNodeUid
                    &&selected.EntryLane==m.Ending.EntryLane&&selected.ExitNodeUid==m.Ending.ExitNodeUid;
                string id=$"lane-merge:{m.Ending.PrefabUid:X16}:{m.Ending.CurveIndices[^1]}:{m.Ending.EntryNodeUid:X16}:{m.Ending.EntryLane}";
                if(m.Left is not null&&m.Kind!=LaneMergeKind.LaneReduction&&!usesEnding&&!approaching.Contains(id))continue;
                var projected=new LanePolyline(edge.Points).Project(m.Position);
                double distance=offset+projected.Along-vehicleAlong;
                if(distance<0){approaching.Remove(id);continue;}
                if(projected.Error>15)continue;
                if(usesEnding)approaching.Add(id);
                result.Add(new(id,m.Ending.PrefabUid,m.Left is null?null:m.Ending.EntryNodeUid,m.Left is null?null:m.Ending.EntryLane,
                    m.Left is null?null:m.Main.EntryNodeUid,m.Left is null?null:m.Main.EntryLane,
                    m.Position,m.Left,m.Count,m.Parallel,m.Kind,distance,m.Reason)
                    {EndDistanceMeters=distance,ApproachLaneCount=ApproachCount(m),Origin=m.Origin,JunctionId="prefab:"+NativeLaneGraph.Prefab(edge),
                        SurvivingEntryOrders=m.SurvivingOrders,AllIncomingLanesDie=m.AllIncomingDie});
            }
            offset+=new LanePolyline(edge.Points).Length;
        }
        approaching.IntersectWith(result.Select(m=>m.Id));
        return result.DistinctBy(m=>m.Id).OrderBy(m=>m.DistanceMeters).ToArray();
    }
    public void Reset()=>approaching.Clear();
    private static int? ApproachCount(Model model)
    {
        int ending=model.Ending.EntryLaneCount,main=model.Main.EntryLaneCount;
        if(ending<=0||main<=0)return null;
        return model.Ending.EntryNodeUid==model.Main.EntryNodeUid?ending==main?ending:null:ending+main;
    }


    private static IReadOnlyList<Model> Build(IReadOnlyList<DirectedRoad> graph,Func<ulong,LinkEts2Nav.Map.MapQueryIssue>? missingConnection)
    {
        var allPaths=graph.Where(e=>!e.ReturnsToEntry).SelectMany(e=>e.LanePaths).ToArray();
        var lanes=allPaths.DistinctBy(p=>(p.PrefabUid,p.EntryNodeUid,p.EntryLane,p.ExitNodeUid,p.ExitLane)).ToArray();
        var result=new List<Model>();var origins=new LaneMergeOriginClassifier(new NativeLaneGraph(graph),missingConnection);
        var continuity=lanes.Where(p=>p.NativeGeometry is not null).GroupBy(p=>p.PrefabUid)
            .ToDictionary(g=>g.Key,g=>LaneContinuity.Analyze(g.First().NativeGeometry!));
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
                double xa=xp.Along[xi],ya=yp.Along[yi];
                var direction=NativeMergeContinuity.Read(x,y,common,continuity.GetValueOrDefault(x.PrefabUid),allPaths);
                if(direction is null)continue;
                bool xEnds=direction.FirstEnds??true;
                var ending=xEnds?x:y;var main=xEnds?y:x;int ei=xEnds?xi:yi,mi=xEnds?yi:xi;
                // The local primitive proves the merge. Classification uses
                // native lane-source mileage, not a geometric parallel trace.
                double? parallel=null;
                var origin=origins.Classify(ending,xEnds?xa:ya,main,xEnds?ya:xa);
                var kind=direction.FirstEnds is null&&!direction.AllDead?LaneMergeKind.Uncertain:origin.Kind;
                bool? left=direction.FirstEnds is null?null:direction.MoveLeft;
                string reason=direction.Reason+"；"+origin.Reason;
                int count=direction.SurvivingOrders.Count;
                result.Add(new(ending,main,ending.Points[ei],left,count,parallel,kind,
                    reason+"；类别按累计500米原生来源窗口判定，不以旧并行测量覆盖",origin,
                    direction.SurvivingOrders,direction.AllIncomingLanesDie));
            }
        }
        return result.DistinctBy(m=>(m.Ending.PrefabUid,m.Ending.EntryNodeUid,m.Ending.EntryLane,m.Ending.ExitNodeUid,m.Ending.ExitLane)).ToArray();
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
