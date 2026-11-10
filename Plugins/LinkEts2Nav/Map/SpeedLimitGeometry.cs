using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

internal static class SpeedLimitGeometry
{
    internal static (double Along,float Error,float Vertical,Vector3 Heading)? Nearest(Vector3 point,DirectedRoad road)
    {
        double along=0; (double Along,float Error,float Vertical,Vector3 Heading)? best=null;
        foreach(var(a,b)in road.Points.Zip(road.Points.Skip(1)))
        {
            var delta=b-a;float length=delta.Length();
            if(length<.001f)continue;
            float t=Math.Clamp(Vector3.Dot(point-a,delta)/delta.LengthSquared(),0,1);
            var p=a+delta*t;
            float error=Vector2.Distance(new(point.X,point.Z),new(p.X,p.Z));
            if(best is null||error<best.Value.Error)best=(along+length*t,error,Math.Abs(point.Y-p.Y),Vector3.Normalize(new(delta.X,0,delta.Z)));
            along+=length;
        }
        return best;
    }
}
