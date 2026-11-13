using System.Globalization;
using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.Map;

/// <summary>
/// Converts confirmed urban traffic-rule volumes into route entry landmarks. This
/// reads only the snapshot's cached areas; CityArea membership alone is not urban.
/// Route lengths use the same directed polyline lengths as navigation progress.
/// </summary>
public static class CityEntryEventBuilder
{
    public const string Kind="city-entry";
    private const double Epsilon=1e-4;
    private sealed record Occupancy(double Start,double End,Vector3 Position,Vector3 Heading,bool EntryKnown);
    private sealed record Area(LimitRegion Region,float MinX,float MaxX,float MinZ,float MaxZ);

    /// <param name="routeOriginAlongMeters">Canonical route coordinate of route[0].Points[0],
    /// not the player's coordinate. Keep this origin stable when trimming a route prefix.</param>
    public static IReadOnlyList<EnvironmentPreviewEvent> Build(IReadOnlyList<DirectedRoad> route,
        double playerAlongMeters,IReadOnlyList<LimitRegion> regions,double routeOriginAlongMeters=0)
    {
        if(!double.IsFinite(playerAlongMeters)||!double.IsFinite(routeOriginAlongMeters))
            throw new ArgumentOutOfRangeException(nameof(playerAlongMeters));
        // Unsupported may describe another rule (e.g. a speed override); an
        // explicitly decoded Urban=true remains valid urban-membership evidence.
        var areas=regions.Where(r=>r.Urban&&r.Points.Count>=3).DistinctBy(r=>r.Id)
            .Select(r=>new Area(r,r.Points.Min(p=>p.X),r.Points.Max(p=>p.X),r.Points.Min(p=>p.Z),r.Points.Max(p=>p.Z))).ToArray();
        if(areas.Length==0)return [];
        var occupied=new List<Occupancy>();double along=routeOriginAlongMeters;
        Vector3? previousEnd=null;bool? previousInside=null;
        foreach(var road in route)
        for(int i=1;i<road.Points.Count;i++)
        {
            var a=road.Points[i-1];var b=road.Points[i];double length=Vector3.Distance(a,b);
            if(!double.IsFinite(length)||length<=0)continue;
            bool connected=previousEnd is {} previous&&Vector3.DistanceSquared(previous,a)<=Epsilon*Epsilon;
            var candidates=areas.Where(r=>Math.Max(a.X,b.X)>=r.MinX&&Math.Min(a.X,b.X)<=r.MaxX
                &&Math.Max(a.Z,b.Z)>=r.MinZ&&Math.Min(a.Z,b.Z)<=r.MaxZ).ToArray();
            var cuts=new List<double>{0,1};
            foreach(var candidate in candidates)
            {
                var r=candidate.Region;
                for(int e=0;e<r.Points.Count;e++)Cut(a,b,r.Points[e],r.Points[(e+1)%r.Points.Count],cuts);
                if(r.Height>0&&b.Y!=a.Y)
                {
                    Add((r.Points[0].Y-a.Y)/(b.Y-a.Y),cuts);
                    Add((r.Points[0].Y+r.Height-a.Y)/(b.Y-a.Y),cuts);
                }
            }
            var ordered=new List<double>();
            foreach(double t in cuts.Order())if(ordered.Count==0||t-ordered[^1]>1e-10)ordered.Add(t);
            bool? last=connected?previousInside:null;
            for(int j=1;j<ordered.Count;j++)
            {
                double start=ordered[j-1],end=ordered[j];
                var mid=Vector3.Lerp(a,b,(float)((start+end)*.5));
                bool inside=candidates.Any(r=>Interior(r.Region,mid));
                if(inside)
                {
                    double from=along+start*length,to=along+end*length;
                    if(occupied.Count>0&&Math.Abs(occupied[^1].End-from)<=Epsilon)
                        occupied[^1]=occupied[^1] with{End=to};
                    else occupied.Add(new(from,to,Vector3.Lerp(a,b,(float)start),Vector3.Normalize(b-a),last==false));
                }
                last=inside;
            }
            previousInside=last;previousEnd=b;along+=length;
        }
        return occupied.Where(o=>o.EntryKnown&&o.Start>=playerAlongMeters-Epsilon)
            .Select(o=>new EnvironmentPreviewEvent(Identity(o),Kind,o.Start,o.Start,Text)).ToArray();
    }

    public static string Text(int meters)=>meters<50?"即将进入城区，请注意减速慢行。":
        NavigationPrompts.AtDistance(ManeuverKind.Straight,meters,0,"进入城区，请注意减速慢行");

    private static string Identity(Occupancy o)
        => string.Create(CultureInfo.InvariantCulture,$"city-entry:{o.Position.X:F2}:{o.Position.Z:F2}:{o.Heading.X:F3}:{o.Heading.Z:F3}:{o.Start:F2}");
    private static void Add(double t,List<double> cuts){if(t>0&&t<1&&double.IsFinite(t))cuts.Add(t);}
    private static double Cross(double ax,double az,double bx,double bz)=>ax*bz-az*bx;
    private static void Cut(Vector3 a,Vector3 b,Vector3 p,Vector3 q,List<double> cuts)
    {
        double dx=(double)b.X-a.X,dz=(double)b.Z-a.Z,ex=(double)q.X-p.X,ez=(double)q.Z-p.Z;
        double px=(double)p.X-a.X,pz=(double)p.Z-a.Z,denominator=Cross(dx,dz,ex,ez);
        if(Math.Abs(denominator)>1e-12*Math.Max(1,Math.Sqrt((dx*dx+dz*dz)*(ex*ex+ez*ez))))
        {
            double t=Cross(px,pz,ex,ez)/denominator,u=Cross(px,pz,dx,dz)/denominator;
            if(u>=-1e-10&&u<=1+1e-10)Add(t,cuts);
        }
        else if(Math.Abs(Cross(px,pz,dx,dz))<=Epsilon*Math.Sqrt(dx*dx+dz*dz))
        {
            double length=dx*dx+dz*dz;
            if(length>0){Add((px*dx+pz*dz)/length,cuts);Add((((double)q.X-a.X)*dx+((double)q.Z-a.Z)*dz)/length,cuts);}
        }
    }
    private static bool Interior(LimitRegion region,Vector3 p)
    {
        if(!region.Contains(p))return false;
        // A road tangent to or following a boundary has not entered its interior.
        for(int i=0;i<region.Points.Count;i++)
        {
            var a=region.Points[i];var b=region.Points[(i+1)%region.Points.Count];
            double dx=(double)b.X-a.X,dz=(double)b.Z-a.Z,px=(double)p.X-a.X,pz=(double)p.Z-a.Z;
            double length=dx*dx+dz*dz;
            if(length>0&&Math.Abs(Cross(px,pz,dx,dz))<=Epsilon*Math.Sqrt(length)
                &&px*dx+pz*dz>=0&&px*dx+pz*dz<=length)return false;
        }
        return true;
    }
}
