using System.Numerics;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Core;

/// <summary>A player-permitted native Hermite curve in world coordinates. Derivatives retain their native scale.</summary>
public sealed record NativeLaneCurve(int Index,Vector3 Start,Vector3 End,Vector3 StartTangent,Vector3 EndTangent,
    IReadOnlyList<int> Next,IReadOnlyList<int> Previous)
{
    public MapQueryIssue? MapIssue {get;init;}
    public LaneBlinker Blinker {get;init;}
    public Vector3 Position(float t)
    {
        float t2=t*t,t3=t2*t;
        return Start+(-2*t3+3*t2)*(End-Start)+(t3-2*t2+t)*StartTangent+(t3-t2)*EndTangent;
    }
    public Vector3 Tangent(float t)
        =>(6*t-6*t*t)*(End-Start)+(3*t*t-4*t+1)*StartTangent+(3*t*t-2*t)*EndTangent;
}
public sealed record NativeLaneGeometry(ulong PrefabUid,IReadOnlyList<NativeLaneCurve> Curves)
{
    public IReadOnlyList<int> InputCurveIndices {get;init;}=[];
    public IReadOnlyList<int> OutputCurveIndices {get;init;}=[];
    internal IReadOnlyList<NativeLanePort> Inputs { get; init; } = [];
    internal IReadOnlyList<NativeLanePort> Outputs { get; init; } = [];
    internal IReadOnlyList<NativeContinuationOverride> ContinuationOverrides {get;init;}=[];
}
internal sealed record NativeLanePort(ulong NodeUid, int Slot, int Order, int CurveIndex);
internal sealed record NativeContinuationOverride(LaneContinuityPointKind Kind,int SharedCurve,int SurvivingBranch);
public enum LaneContinuityPointKind { Merge,Split }
public enum LaneContinuityRole { Survivor,Dead,Newborn,Unknown }
public enum LaneTurnKind { Unknown,ForwardLeft,ForwardRight,Left,Right,RearLeft,RearRight }
public sealed record LaneTurnDescription(LaneTurnKind Turn,int? SourceCurveIndex,MapQueryIssue? MapIssue=null);
public sealed record LaneContinuityBranch(int CurveIndex,LaneContinuityRole Role,double? AngleDegrees,
    double? NormalizedAngle,LaneTurnKind Turn)
{
    public MapQueryIssue? MapIssue {get;init;}
    public int? BlinkerSourceCurveIndex {get;init;}
    public bool VetoedByBlinker=>BlinkerSourceCurveIndex.HasValue;
    public int? TurnSourceCurveIndex {get;init;}
}
public sealed record LaneContinuityPoint(ulong PrefabUid,LaneContinuityPointKind Kind,int SharedCurveIndex,
    Vector3 Origin,Vector3 StandardTangent,IReadOnlyList<LaneContinuityBranch> Branches)
{
    public MapQueryIssue? MapIssue {get;init;}
}
public sealed record LaneContinuityResult(IReadOnlyList<LaneContinuityPoint> Points,IReadOnlyList<int> UnchangedCurveIndices)
{
    public MapQueryIssue? MapIssue {get;init;}

    internal (int Index,LaneContinuityPoint Point,LaneContinuityBranch Branch)? FirstSideSplit(IReadOnlyList<int> curves)
    {
        for(int i=1;i<curves.Count;i++)
        {
            var split=Points.FirstOrDefault(p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==curves[i-1]);
            var branch=split?.Branches.FirstOrDefault(b=>b.CurveIndex==curves[i]);
            if(branch is {Role:LaneContinuityRole.Newborn or LaneContinuityRole.Unknown})return (i,split!,branch);
        }
        return null;
    }
}

/// <summary>Local N-to-one continuity, and its one-to-N counterpart. Physical links are never removed.</summary>
public static class LaneContinuity
{
    private const double ZeroAngle=0;
    private static MapQueryFailure Invalid(string reason)=>new(MapQueryFailureKind.InvalidData,reason);
    public static LaneContinuityResult Analyze(NativeLaneGeometry geometry)
    {
        if(geometry.Curves.Count==0)return new([],[]){MapIssue=new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,$"Prefab {geometry.PrefabUid:X16}: native lane geometry is unavailable.")};
        var duplicate=geometry.Curves.GroupBy(c=>c.Index).FirstOrDefault(g=>g.Count()!=1);
        if(duplicate is not null)return new([],[]){MapIssue=Invalid($"Prefab {geometry.PrefabUid:X16}: duplicate native curve {duplicate.Key}.")};
        var curves=geometry.Curves.ToDictionary(c=>c.Index);var points=new List<LaneContinuityPoint>();var involved=new HashSet<int>();
        var incoming=geometry.Curves.SelectMany(c=>c.Next.Select(next=>(next,c.Index))).ToLookup(p=>p.next,p=>p.Index);
        var outgoing=geometry.Curves.SelectMany(c=>c.Previous.Select(previous=>(previous,c.Index))).ToLookup(p=>p.previous,p=>p.Index);
        foreach(var shared in geometry.Curves.OrderBy(c=>c.Index))
        {
            foreach(var kind in new[]{LaneContinuityPointKind.Merge,LaneContinuityPointKind.Split})
            {
                // Both declarations identify the same native links after input validation.
                var indices=(kind==LaneContinuityPointKind.Merge
                    ?shared.Previous.Concat(incoming[shared.Index])
                    :shared.Next.Concat(outgoing[shared.Index]))
                    .Distinct().Order().ToArray();
                if(indices.Length<2)continue;
                foreach(int index in indices)involved.Add(index);
                points.Add(Evaluate(geometry,shared,kind,indices,curves,incoming,outgoing));
            }
        }
        return new(points.ToArray(),geometry.Curves.Where(c=>c.MapIssue is null&&!involved.Contains(c.Index)).Select(c=>c.Index).Order().ToArray())
            {MapIssue=geometry.Curves.Select(c=>c.MapIssue).FirstOrDefault(issue=>issue is not null)};
    }
    private static LaneContinuityPoint Evaluate(NativeLaneGeometry geometry,NativeLaneCurve shared,LaneContinuityPointKind kind,
        int[] indices,IReadOnlyDictionary<int,NativeLaneCurve> curves,ILookup<int,int> incoming,ILookup<int,int> outgoing)
    {
        bool merge=kind==LaneContinuityPointKind.Merge;
        ulong prefab=geometry.PrefabUid;
        Vector3 origin=merge?shared.Start:shared.End,standard=merge?shared.StartTangent:shared.EndTangent;
        var angles=new double?[indices.Length];var vetoes=new int?[indices.Length];
        NativeLaneApproachTrace.AssertReciprocal(curves,shared,indices,!merge);
        LaneContinuityBranch KnownDead(int id,int source)
        {
            var direction=DescribeApproach(geometry,curves[id],shared.Index,standard,origin,true,curves,incoming,outgoing);
            return new(id,LaneContinuityRole.Dead,null,null,direction.Turn)
                {BlinkerSourceCurveIndex=source,TurnSourceCurveIndex=direction.SourceCurveIndex,MapIssue=direction.MapIssue};
        }
        LaneContinuityPoint Unknown(MapQueryIssue issue)=>new(prefab,kind,shared.Index,origin,standard,
            indices.Select((id,i)=>vetoes[i] is {} source
                ?KnownDead(id,source)
                :new LaneContinuityBranch(id,LaneContinuityRole.Unknown,null,null,LaneTurnKind.Unknown){MapIssue=issue}).ToArray()){MapIssue=issue};
        if(shared.MapIssue is {} sharedIssue)return Unknown(sharedIssue);
        if(!float.IsFinite(origin.X)||!float.IsFinite(origin.Y)||!float.IsFinite(origin.Z))return Unknown(Invalid($"Prefab {prefab:X16} curve {shared.Index}: nonfinite origin."));
        if(!Horizontal(standard,out var sx,out var sz))return Unknown(Invalid($"Prefab {prefab:X16} curve {shared.Index}: missing horizontal standard tangent."));
        MapQueryIssue? eligibilityIssue=null;
        for(int i=0;i<indices.Length;i++)
        {
            var branch=curves[indices[i]];
            if(branch.MapIssue is {} issue)return Unknown(issue);
            if(merge)
            {
                var veto=FindMergeBlinker(branch,shared.Index,geometry.InputCurveIndices,curves,incoming,outgoing);
                eligibilityIssue??=veto.Issue;
                vetoes[i]=veto.Source;
            }
        }
        if(eligibilityIssue is not null)return Unknown(eligibilityIssue);
        for(int i=0;i<indices.Length;i++)
        {
            if(vetoes[i] is not null)continue; // dead before any midpoint/normalization work
            var branch=curves[indices[i]];
            if(Horizontal(branch.Tangent(.5f),out var x,out var z))angles[i]=Math.Atan2(Math.Abs(x*sz-z*sx),x*sx+z*sz)*180/Math.PI;
            else return Unknown(Invalid($"Prefab {prefab:X16} curve {branch.Index}: missing horizontal midpoint tangent."));
        }
        var candidates=Enumerable.Range(0,indices.Length).Where(i=>vetoes[i] is null).ToArray();
        double? minimum=candidates.Length==0?null:candidates.Min(i=>angles[i]!.Value);
        var zeros=candidates.Where(i=>angles[i]<=ZeroAngle).ToArray();
        var qualified=zeros.Length>0?zeros:candidates.Where(i=>angles[i]/minimum<2).ToArray();
        int survivor=qualified.Length==1?qualified[0]:-1;
        if(geometry.ContinuationOverrides.Count>0&&geometry.ContinuationOverrides.FirstOrDefault(o=>
            o.Kind==kind&&o.SharedCurve==shared.Index) is { } correction)
        {
            survivor=Array.IndexOf(indices,correction.SurvivingBranch);
            if(survivor<0||vetoes[survivor].HasValue)
                throw new InvalidDataException($"Prefab {prefab:X16} continuity override names unavailable curve {correction.SurvivingBranch} at {shared.Index}.");
        }
        var result=new List<LaneContinuityBranch>();
        for(int i=0;i<indices.Length;i++)
        {
            var branch=curves[indices[i]];bool alive=i==survivor;
            LaneTurnKind turn=LaneTurnKind.Unknown;MapQueryIssue? issue=null;int? turnSource=null;
            if(!alive)
            {
                var direction=DescribeApproach(geometry,branch,shared.Index,standard,origin,merge,curves,incoming,outgoing);
                turn=direction.Turn;issue=direction.MapIssue;turnSource=direction.SourceCurveIndex;
            }
            result.Add(new(branch.Index,alive?LaneContinuityRole.Survivor:merge?LaneContinuityRole.Dead:LaneContinuityRole.Newborn,
                angles[i],vetoes[i] is not null||minimum is null?null:minimum<=ZeroAngle?(angles[i]<=ZeroAngle?1:(double?)null):angles[i]/minimum,turn)
                {MapIssue=issue,BlinkerSourceCurveIndex=vetoes[i],TurnSourceCurveIndex=turnSource});
        }
        return new(prefab,kind,shared.Index,origin,standard,result.ToArray());
    }
    private static (int? Source,MapQueryIssue? Issue) FindMergeBlinker(NativeLaneCurve branch,int target,IReadOnlyList<int> inputs,
        IReadOnlyDictionary<int,NativeLaneCurve> curves,ILookup<int,int> incoming,ILookup<int,int> outgoing)
    {
        MapQueryIssue? unresolved=null;
        foreach(var step in NativeLaneApproachTrace.Walk(branch,target,curves,incoming,outgoing,false,inputs,false))
        {
            if(step.Issue is {} traceIssue)return (null,traceIssue);
            var current=step.Curve!;
#if DEBUG
            if(!Enum.IsDefined(current.Blinker))
                throw new NavigationContractException($"Native curve {current.Index}: unknown Blinker value {(int)current.Blinker}.");
#endif
            if(current.Blinker is LaneBlinker.Left or LaneBlinker.Right)return (current.Index,null);
            unresolved??=current.MapIssue;
        }
        return (null,unresolved);
    }
    /// <summary>Describes a complete approach/departure segment without changing lane qualification.</summary>
    public static LaneTurnDescription DescribeTurn(NativeLaneGeometry geometry,int branchCurveIndex,Vector3 standard,Vector3 origin,bool merge)
    {
        if(geometry.Curves.GroupBy(c=>c.Index).Any(g=>g.Count()!=1))return new(LaneTurnKind.Unknown,null,Invalid("Native turn geometry contains duplicate curve indices."));
        var curves=geometry.Curves.ToDictionary(c=>c.Index);
        if(!curves.TryGetValue(branchCurveIndex,out var branch))return new(LaneTurnKind.Unknown,null,Invalid($"Native turn curve {branchCurveIndex} is unavailable."));
        var incoming=geometry.Curves.SelectMany(c=>c.Next.Select(next=>(next,c.Index))).ToLookup(p=>p.next,p=>p.Index);
        var outgoing=geometry.Curves.SelectMany(c=>c.Previous.Select(previous=>(previous,c.Index))).ToLookup(p=>p.previous,p=>p.Index);
        return DescribeApproach(geometry,branch,null,standard,origin,merge,curves,incoming,outgoing);
    }
    private static LaneTurnDescription DescribeApproach(NativeLaneGeometry geometry,NativeLaneCurve branch,int? target,
        Vector3 standard,Vector3 origin,bool merge,IReadOnlyDictionary<int,NativeLaneCurve> curves,ILookup<int,int> incoming,ILookup<int,int> outgoing)
    {
        NativeLaneCurve? boundary=null;
        foreach(var step in NativeLaneApproachTrace.Walk(branch,target,curves,incoming,outgoing,!merge,
            merge?geometry.InputCurveIndices:geometry.OutputCurveIndices,true))
        {
            if(step.Issue is {} issue)return new(LaneTurnKind.Unknown,null,issue);
            if(step.Curve!.MapIssue is {} curveIssue)return new(LaneTurnKind.Unknown,null,curveIssue);
            boundary=step.Curve;
        }
        if(boundary is null)
        {
            // A merge and a split can share the same endpoint. Only the two rays
            // at that endpoint belong to this movement; an earlier bend may be common to other exits.
            var tangent = merge ? branch.EndTangent : branch.StartTangent;
            var local = DescribeDirection(tangent, standard, Vector3.Zero, merge);
            return new(local, branch.Index, local == LaneTurnKind.Unknown
                ? new MapQueryFailure(MapQueryFailureKind.Ambiguous,
                    $"Prefab {geometry.PrefabUid:X16} curve {branch.Index}: coincident topology origins have no distinct lateral turn.")
                : null);
        }
        var turn=DescribeTurn(boundary,standard,origin,merge);
        return new(turn,boundary.Index,turn==LaneTurnKind.Unknown?Invalid($"Prefab {geometry.PrefabUid:X16} curve {boundary.Index}: turn side cannot be established from native tangent and origin."):null);
    }
    /// <summary>Describes actual travel side against a supplied standard without changing lane qualification.</summary>
    public static LaneTurnKind DescribeTurn(NativeLaneCurve branch,Vector3 standard,Vector3 origin,bool merge)
    {
        static bool Finite(Vector3 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y)&&float.IsFinite(value.Z);
        if(!Finite(origin)||!Finite(branch.Start)||!Finite(branch.End))return LaneTurnKind.Unknown;
        Vector3 direction=merge?branch.StartTangent:branch.EndTangent;
        return DescribeDirection(direction, standard, merge ? origin - branch.Start : branch.End - origin, merge);
    }
    private static LaneTurnKind DescribeDirection(Vector3 direction, Vector3 standard, Vector3 offset, bool merge)
    {
        if(!Horizontal(direction,out double dx,out double dz)||!Horizontal(standard,out double sx,out double sz))return LaneTurnKind.Unknown;
        double cross=merge?dx*sz-dz*sx:sx*dz-sz*dx;
        double angle=Math.Atan2(Math.Abs(cross),dx*sx+dz*sz)*180/Math.PI;
        if(angle<=1||angle>=179)
        {
            // A merge is relative to its arriving lane's start ray. A split is
            // relative to the common arriving ray, including a branch that ends
            // backwards: its final heading must not invert the physical side.
            cross=merge?dx*offset.Z-dz*offset.X:sx*offset.Z-sz*offset.X;
        }
        if(cross==0)return LaneTurnKind.Unknown;
        bool right=cross>0;
        if(angle<=45)return right?LaneTurnKind.ForwardRight:LaneTurnKind.ForwardLeft;
        if(angle<135)return right?LaneTurnKind.Right:LaneTurnKind.Left;
        return right?LaneTurnKind.RearRight:LaneTurnKind.RearLeft;
    }
    private static bool Horizontal(Vector3 value,out double x,out double z)
    {
        x=value.X;z=value.Z;
        if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z))return false;
        double norm=Math.Sqrt(x*x+z*z);if(norm==0)return false;x/=norm;z/=norm;return true;
    }
}
