using System.Numerics;

namespace LinkEts2Nav.Core;

/// <summary>A side entrance joins an existing lane inside this prefab, without an added parallel lane.</summary>
public static class DirectJunctionJoin
{
    public sealed record Decision(DirectedRoad Main, NavigationSide TrafficSide);

    public static Decision? Find(DirectedRoad side,IReadOnlyList<DirectedRoad> alternatives)
    {
        if(side.ReturnsToEntry||side.LanePaths.Count==0)return null;
        var proven=new List<DirectedRoad>();
        foreach(var main in alternatives.Where(e=>e.StartNodeUid!=side.StartNodeUid&&e.EndNodeUid==side.EndNodeUid))
        {
            // Every usable side lane must enter an already existing native chain.
            // Merely reaching the same control node or output lane is insufficient.
            if(side.LanePaths.All(a=>main.LanePaths.Any(b=>SharesMainTail(a,b))))proven.Add(main);
        }
        if(proven.Select(e=>e.StartNodeUid).Distinct().Count()!=1)return null;
        var chosen=proven[0];
        var approach=Horizontal(side.NativeEntryTangent);var mainApproach=Horizontal(chosen.NativeEntryTangent);
        if(approach is null||mainApproach is null)return null;
        double cross=approach.Value.X*-mainApproach.Value.Y-approach.Value.Y*-mainApproach.Value.X;
        var traffic=Math.Abs(cross)<.1?NavigationSide.None:cross<0?NavigationSide.Left:NavigationSide.Right;
        return new(chosen,traffic);
    }

    private static bool SharesMainTail(PrefabLanePath side,PrefabLanePath main)
    {
        if(side.PrefabUid!=main.PrefabUid||side.ExitLane!=main.ExitLane||side.ExitNodeUid!=main.ExitNodeUid)return false;
        int count=0;
        while(count<Math.Min(side.CurveIndices.Count,main.CurveIndices.Count)
            &&side.CurveIndices[^(count+1)]==main.CurveIndices[^(count+1)])count++;
        return count>0&&MergeBlinkerDirection.Classify(side,main,count).FirstEnds==true;
    }

    private static Vector2? Horizontal(Vector3? value)
    {
        if(value is not {} v||!float.IsFinite(v.X)||!float.IsFinite(v.Z))return null;
        var horizontal=new Vector2(v.X,v.Z);
        return horizontal.LengthSquared()>1e-8?Vector2.Normalize(horizontal):null;
    }
}
