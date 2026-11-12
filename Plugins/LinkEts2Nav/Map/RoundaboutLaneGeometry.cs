using System.Numerics;
using ETS2LA.Game.Utils;

namespace LinkEts2Nav.Map;

public sealed record RoundaboutLaneGeometryInfo(string Candidate,int CorrectedRoads,
    double ReferenceCircularity,double LaneCircularity,double ReferenceSeamsMeters,double LaneSeamsMeters,
    string? Uncertainty);

/// <summary>
/// Circularity geometry only. PPD endpoints anchor the lane selected by the reference
/// circuit; adjoining ordinary road runs interpolate those lateral offsets using the
/// same native Road.InterpolateCurve basis and offset formula as ParsedRoad.InterpolateLane.
/// No lane width/default variant/global SII cache is guessed or changed here.
/// This sampled offset curve is not a cubic: NativeCurve, its integrated centre,
/// and every open-path angular weight in RoundaboutDirection remain unchanged.
/// </summary>
public static class RoundaboutLaneGeometry
{
    public sealed record Result(Vector3[] Points,RoundaboutLaneGeometryInfo Info);

    public static Result Build(IReadOnlyDictionary<RoundaboutCurveKey,RoundaboutCurve> graph,
        IReadOnlyList<RoundaboutCurveKey> ring)
    {
        var curves=ring.Select(k=>graph[k]).ToArray();
        var original=curves.Select(c=>c.Points.ToArray()).ToArray();
        var corrected=original.Select(p=>p.ToArray()).ToArray();
        int count=curves.Length,changed=0;string? uncertainty=null;
        int anchor=Array.FindIndex(curves,c=>c.PrefabUid is not null);
        if(anchor<0)uncertainty="No native prefab lane endpoint anchors.";
        // Starting after a prefab makes every road run finite, including a run
        // crossing the reference ring's arbitrary start/end array boundary.
        for(int cursor=1;anchor>=0&&cursor<count;)
        {
            int first=(anchor+cursor)%count;
            if(curves[first].PrefabUid is not null){cursor++;continue;}
            var indices=new List<int>();
            while(cursor<count&&curves[(anchor+cursor)%count].PrefabUid is null)
                indices.Add((anchor+cursor++)%count);
            int before=(first+count-1)%count,after=(anchor+cursor)%count;
            bool Valid(int index)=>curves[index].RoadNormals is { } normals
                &&normals.Count==original[index].Length&&normals.Count>=2
                &&normals.All(n=>float.IsFinite(n.X)&&float.IsFinite(n.Y)&&float.IsFinite(n.Z)&&Math.Abs(n.LengthSquared()-1)<.001f);
            if(indices.Any(i=>!Valid(i)))
            {uncertainty??="Road interpolation basis unavailable; that road run retains reference geometry.";continue;}
            var lengths=indices.Select(i=>Length(original[i])).ToArray();double total=lengths.Sum();
            if(total<=0){uncertainty??="Zero-length road run.";continue;}
            int last=indices[^1];
            float start=Vector3.Dot(original[first][0]-original[before][^1],curves[first].RoadNormals![0]);
            float end=Vector3.Dot(original[last][^1]-original[after][0],curves[last].RoadNormals![^1]);
            double traversed=0;
            for(int r=0;r<indices.Count;r++)
            {
                int index=indices[r];var source=original[index];var normals=curves[index].RoadNormals!;
                float from=RoadUtils.Lerp(start,end,(float)(traversed/total));
                traversed+=lengths[r];
                float to=RoadUtils.Lerp(start,end,(float)(traversed/total));
                for(int p=0;p<source.Length;p++)
                    corrected[index][p]=source[p]-normals[p]*RoadUtils.Lerp(from,to,(float)p/(source.Length-1));
                changed++;
            }
        }
        var reference=original.SelectMany(p=>p).ToArray();var points=corrected.SelectMany(p=>p).ToArray();
        string id=string.Join("/",ring.Select(k=>k.ItemUid).Distinct().Order().Select(k=>$"{k:X16}"));
        return new(points,new(id,changed,RoundaboutBuilder.Circularity(reference),RoundaboutBuilder.Circularity(points),
            Seams(original),Seams(corrected),uncertainty));
    }
    private static double Length(Vector3[] p)=>p.Zip(p.Skip(1)).Sum(pair=>(double)Vector3.Distance(pair.First,pair.Second));
    private static double Seams(Vector3[][] paths)
        => paths.Select((p,i)=>paths.Length==0||p.Length==0||paths[(i+1)%paths.Length].Length==0?0:
            Horizontal(p[^1]-paths[(i+1)%paths.Length][0])).Sum();
    private static double Horizontal(Vector3 v)=>Math.Sqrt((double)v.X*v.X+(double)v.Z*v.Z);
}
