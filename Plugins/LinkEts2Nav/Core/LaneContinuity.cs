using System.Numerics;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

/// <summary>A player-permitted native Hermite curve in world coordinates. Derivatives retain their native scale.</summary>
public sealed record NativeLaneCurve(int Index,Vector3 Start,Vector3 End,Vector3 StartTangent,Vector3 EndTangent,
    IReadOnlyList<int> Next,IReadOnlyList<int> Previous)
{
    public MapQueryIssue? MapIssue {get;init;}
    public Vector3 Position(float t)
    {
        float t2=t*t,t3=t2*t;
        return Start+(-2*t3+3*t2)*(End-Start)+(t3-2*t2+t)*StartTangent+(t3-t2)*EndTangent;
    }
    public Vector3 Tangent(float t)
        =>(6*t-6*t*t)*(End-Start)+(3*t*t-4*t+1)*StartTangent+(3*t*t-2*t)*EndTangent;
}
public sealed record NativeLaneGeometry(ulong PrefabUid,IReadOnlyList<NativeLaneCurve> Curves);
public enum LaneContinuityPointKind { Merge,Split }
public enum LaneContinuityRole { Survivor,Dead,Newborn,Unknown }
public enum LaneTurnKind { Unknown,ForwardLeft,ForwardRight,Left,Right,RearLeft,RearRight }
public sealed record LaneContinuityBranch(int CurveIndex,LaneContinuityRole Role,double? AngleDegrees,
    double? NormalizedAngle,LaneTurnKind Turn)
{
    public MapQueryIssue? MapIssue {get;init;}
}
public sealed record LaneContinuityPoint(ulong PrefabUid,LaneContinuityPointKind Kind,int SharedCurveIndex,
    Vector3 Origin,Vector3 StandardTangent,IReadOnlyList<LaneContinuityBranch> Branches)
{
    public MapQueryIssue? MapIssue {get;init;}
}
public sealed record LaneContinuityResult(IReadOnlyList<LaneContinuityPoint> Points,IReadOnlyList<int> UnchangedCurveIndices)
{
    public MapQueryIssue? MapIssue {get;init;}
}

/// <summary>Local N-to-one continuity, and its one-to-N counterpart. Physical links are never removed.</summary>
public static class LaneContinuity
{
    private const double ZeroAngle=1e-9;
    private static MapQueryFailure Invalid(string reason)=>new(MapQueryFailureKind.InvalidData,reason);
    public static LaneContinuityResult Analyze(NativeLaneGeometry geometry)
    {
        var duplicate=geometry.Curves.GroupBy(c=>c.Index).FirstOrDefault(g=>g.Count()!=1);
        if(duplicate is not null)return new([],[]){MapIssue=Invalid($"Prefab {geometry.PrefabUid:X16}: duplicate native curve {duplicate.Key}.")};
        var curves=geometry.Curves.ToDictionary(c=>c.Index);var points=new List<LaneContinuityPoint>();var involved=new HashSet<int>();
        var incoming=geometry.Curves.SelectMany(c=>c.Next.Select(next=>(next,c.Index))).ToLookup(p=>p.next,p=>p.Index);
        var outgoing=geometry.Curves.SelectMany(c=>c.Previous.Select(previous=>(previous,c.Index))).ToLookup(p=>p.previous,p=>p.Index);
        foreach(var shared in geometry.Curves.OrderBy(c=>c.Index))
        {
            foreach(var kind in new[]{LaneContinuityPointKind.Merge,LaneContinuityPointKind.Split})
            {
                // Consider declarations on both ends. A missing reciprocal declaration must
                // become Unknown, never silently erase a real N-way choice.
                var indices=(kind==LaneContinuityPointKind.Merge
                    ?shared.Previous.Concat(incoming[shared.Index])
                    :shared.Next.Concat(outgoing[shared.Index]))
                    .Distinct().Order().ToArray();
                if(indices.Length<2)continue;
                foreach(int index in indices)involved.Add(index);
                points.Add(Evaluate(geometry.PrefabUid,shared,kind,indices,curves));
            }
        }
        return new(points.ToArray(),geometry.Curves.Select(c=>c.Index).Where(i=>!involved.Contains(i)).Order().ToArray());
    }
    private static LaneContinuityPoint Evaluate(ulong prefab,NativeLaneCurve shared,LaneContinuityPointKind kind,
        int[] indices,IReadOnlyDictionary<int,NativeLaneCurve> curves)
    {
        bool merge=kind==LaneContinuityPointKind.Merge;
        Vector3 origin=merge?shared.Start:shared.End,standard=merge?shared.StartTangent:shared.EndTangent;
        LaneContinuityPoint Unknown(MapQueryIssue issue)=>new(prefab,kind,shared.Index,origin,standard,
            indices.Select(i=>new LaneContinuityBranch(i,LaneContinuityRole.Unknown,null,null,LaneTurnKind.Unknown){MapIssue=issue}).ToArray()){MapIssue=issue};
        if(shared.MapIssue is {} sharedIssue)return Unknown(sharedIssue);
        if(!float.IsFinite(origin.X)||!float.IsFinite(origin.Y)||!float.IsFinite(origin.Z))return Unknown(Invalid($"Prefab {prefab:X16} curve {shared.Index}: nonfinite origin."));
        if(!Horizontal(standard,out var sx,out var sz))return Unknown(Invalid($"Prefab {prefab:X16} curve {shared.Index}: missing horizontal standard tangent."));
        var angles=new double[indices.Length];
        for(int i=0;i<indices.Length;i++)
        {
            if(!curves.TryGetValue(indices[i],out var branch))return Unknown(Invalid($"Prefab {prefab:X16}: native curve {indices[i]} is unavailable."));
            if(branch.MapIssue is {} issue)return Unknown(issue);
            if(!(merge?branch.Next:branch.Previous).Contains(shared.Index)
                ||!(merge?shared.Previous:shared.Next).Contains(branch.Index))
                return Unknown(Invalid($"Prefab {prefab:X16}: native link {indices[i]} / {shared.Index} is not reciprocal."));
            if(!Horizontal(branch.Tangent(.5f),out var x,out var z))return Unknown(Invalid($"Prefab {prefab:X16} curve {branch.Index}: missing horizontal midpoint tangent."));
            angles[i]=Math.Atan2(Math.Abs(x*sz-z*sx),x*sx+z*sz)*180/Math.PI;
        }
        double minimum=angles.Min();var zeros=angles.Select((angle,i)=>(angle,i)).Where(p=>p.angle<=ZeroAngle).Select(p=>p.i).ToArray();
        var qualified=zeros.Length>0?zeros:Enumerable.Range(0,indices.Length).Where(i=>angles[i]/minimum<2).ToArray();
        int survivor=qualified.Length==1?qualified[0]:-1;
        var result=new List<LaneContinuityBranch>();
        for(int i=0;i<indices.Length;i++)
        {
            var branch=curves[indices[i]];bool alive=i==survivor;
            LaneTurnKind turn=LaneTurnKind.Unknown;MapQueryIssue? issue=null;
            if(!alive)
            {
                turn=Turn(branch,standard,origin,merge);
                if(turn==LaneTurnKind.Unknown)issue=Invalid($"Prefab {prefab:X16} curve {branch.Index}: turn side cannot be established from native tangent and origin.");
            }
            result.Add(new(branch.Index,alive?LaneContinuityRole.Survivor:merge?LaneContinuityRole.Dead:LaneContinuityRole.Newborn,
                angles[i],minimum<=ZeroAngle?(angles[i]<=ZeroAngle?1:(double?)null):angles[i]/minimum,turn){MapIssue=issue});
        }
        return new(prefab,kind,shared.Index,origin,standard,result.ToArray());
    }
    private static LaneTurnKind Turn(NativeLaneCurve branch,Vector3 standard,Vector3 origin,bool merge)
    {
        Vector3 direction=merge?branch.StartTangent:branch.EndTangent;
        if(!Horizontal(direction,out double dx,out double dz)||!Horizontal(standard,out double sx,out double sz))return LaneTurnKind.Unknown;
        double cross=merge?dx*sz-dz*sx:sx*dz-sz*dx;
        double angle=Math.Atan2(Math.Abs(cross),dx*sx+dz*sz)*180/Math.PI;
        if(angle<=1||angle>=179)
        {
            // Mirror travel for a split, then restore its actual left/right sense.
            Vector3 offset=merge?origin-branch.Start:branch.End-origin;
            cross=dx*offset.Z-dz*offset.X;
        }
        if(Math.Abs(cross)<=1e-10)return LaneTurnKind.Unknown;
        bool right=cross>0;
        if(angle<=45)return right?LaneTurnKind.ForwardRight:LaneTurnKind.ForwardLeft;
        if(angle<135)return right?LaneTurnKind.Right:LaneTurnKind.Left;
        return right?LaneTurnKind.RearRight:LaneTurnKind.RearLeft;
    }
    private static bool Horizontal(Vector3 value,out double x,out double z)
    {
        x=value.X;z=value.Z;
        if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z))return false;
        double norm=Math.Sqrt(x*x+z*z);if(norm<=1e-10)return false;x/=norm;z/=norm;return true;
    }
}
