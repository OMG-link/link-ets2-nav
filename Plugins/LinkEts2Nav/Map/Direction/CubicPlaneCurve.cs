namespace LinkEts2Nav.Map;

/// <summary>Double polynomial coefficients in the right-handed horizontal plane (world X, -world Z).</summary>
public readonly record struct PlanePoint(double X, double Y)
{
    public static PlanePoint operator +(PlanePoint a, PlanePoint b) => new(a.X+b.X,a.Y+b.Y);
    public static PlanePoint operator -(PlanePoint a, PlanePoint b) => new(a.X-b.X,a.Y-b.Y);
    public static PlanePoint operator *(double k, PlanePoint a) => new(k*a.X,k*a.Y);
    public static PlanePoint operator *(PlanePoint a,double k) => k*a;
}

public sealed record CubicPlaneCurve(PlanePoint A, PlanePoint B, PlanePoint C, PlanePoint D)
{
    public PlanePoint At(double t) => ((t*A+B)*t+C)*t+D;
    public static CubicPlaneCurve Hermite(PlanePoint p, PlanePoint q, PlanePoint u, PlanePoint v)
    {
        var delta=q-p;
        return new(-2*delta+u+v,3*delta-2*u-v,u,p);
    }
    public CubicPlaneCurve Shift(PlanePoint origin) => this with { D=D-origin };
    public CubicPlaneCurve Reverse() => new(-1*A,3*A+B,-3*A-2*B-C,A+B+C+D);
    public CubicPlaneCurve Rotate(double angle)
    {
        double s=Math.Sin(angle),c=Math.Cos(angle);
        PlanePoint R(PlanePoint p)=>new(c*p.X-s*p.Y,s*p.X+c*p.Y);
        return new(R(A),R(B),R(C),R(D));
    }
    internal double[] X => [D.X,C.X,B.X,A.X];
    internal double[] Y => [D.Y,C.Y,B.Y,A.Y];
}

/// <summary>Native polynomial integration and branch-cut winding. No sampled polyline is used.</summary>
public static class CubicWinding
{
    // Local metre tolerance describes the numerical domain; a curve through the centre
    // has no defined winding and must be reported as indeterminate, never silently accepted.
    public const double PositionTolerance=1e-8;
    public const double AngleTolerance=1e-10;
    public static PlanePoint Centroid(IReadOnlyList<CubicPlaneCurve> circuit)
    {
        if(circuit.Count==0)throw new InvalidDataException("Empty reference circuit.");
        var origin=circuit[0].D; var areas=new List<double>();var xs=new List<double>();var ys=new List<double>();
        foreach(var world in circuit)
        {
            var c=world.Shift(origin);var x=c.X;var y=c.Y;
            var dx=Derivative(x);var dy=Derivative(y);
            areas.Add(Integral(Product(x,dy))-Integral(Product(y,dx)));
            xs.Add(Integral(Product(Product(x,x),dy)));
            ys.Add(-Integral(Product(Product(y,y),dx)));
        }
        double twiceArea=Sum(areas),mx=Sum(xs),my=Sum(ys);
        if(!double.IsFinite(twiceArea)||!double.IsFinite(mx)||!double.IsFinite(my)||Math.Abs(twiceArea)<PositionTolerance*PositionTolerance)
            throw new InvalidDataException("Reference circuit has numerically zero signed area.");
        return origin+new PlanePoint(mx/twiceArea,my/twiceArea);
    }
    public static double Rotation(IReadOnlyList<CubicPlaneCurve> localCurves)
    {
        // A reproducible rotated cut, chosen against every endpoint rather than a
        // fixed world axis. This changes only coordinates used by the calculation.
        ulong random=0x8f3d9a17b621c405UL;
        for(int i=0;i<1024;i++)
        {
            random^=random<<13;random^=random>>7;random^=random<<17;
            double angle=(random>>11)*(2*Math.PI/(1UL<<53));
            if(localCurves.All(c=>new[]{c.D,c.At(1)}.All(p=>
            {
                double y=Math.Sin(angle)*p.X+Math.Cos(angle)*p.Y;
                return Math.Abs(y)>PositionTolerance*16;
            })))return angle;
        }
        throw new InvalidDataException("No numerically separated branch cut for curve endpoints.");
    }
    public static double Angle(CubicPlaneCurve localRotated)
    {
        EnsureCentreSeparated(localRotated);
        var x=localRotated.X;var y=localRotated.Y;
        var roots=Roots(y);double adjustment=0;
        foreach(double t in roots)
        {
            double xx=Evaluate(x,t);
            if(Math.Abs(xx)<=PositionTolerance)throw new InvalidDataException("Native curve passes through the winding centre.");
            if(t<=0||t>=1||xx>=0)continue;
            int index=Array.IndexOf(roots,t);
            double before=Evaluate(y,((index==0?0:roots[index-1])+t)*.5);
            double after=Evaluate(y,(t+(index+1==roots.Length?1:roots[index+1]))*.5);
            // Signs on both sides distinguish an actual crossing from a double
            // root (tangency), including when derivative itself is almost zero.
            if(before<0&&after>0)adjustment-=2*Math.PI;
            if(before>0&&after<0)adjustment+=2*Math.PI;
        }
        // A centre on a horizontal curve would not appear as an isolated y root.
        if(y.All(v=>Math.Abs(v)<=PositionTolerance)&&Roots(x).Length>0)
            throw new InvalidDataException("Native curve passes through the winding centre.");
        var p=localRotated.D;var q=localRotated.At(1);
        if(double.Hypot(p.X,p.Y)<=PositionTolerance||double.Hypot(q.X,q.Y)<=PositionTolerance)
            throw new InvalidDataException("Curve endpoint is the winding centre.");
        return Math.Atan2(q.Y,q.X)-Math.Atan2(p.Y,p.X)+adjustment;
    }
    /// <summary>All real roots in [0,1], isolated by derivative monotonic intervals.
    /// Bisection stops at adjacent doubles; no coarse sampling or closed-form cubic cancellation.</summary>
    internal static double[] Roots(double[] coefficients)
    {
        int degree=coefficients.Length-1;
        while(degree>0&&coefficients[degree]==0)degree--;
        if(degree==0)return [];
        var c=coefficients.Take(degree+1).ToArray();
        var cuts=new[]{0d}.Concat(Roots(Derivative(c)).Where(t=>t>0&&t<1)).Append(1d).ToArray();
        var roots=new List<double>();
        // 64 binary64 unit roundoffs. double.Epsilon is a subnormal, not this value.
        double scale=c.Sum(Math.Abs),epsilon=scale*1.4210854715202004e-14;
        var crossings=new bool[cuts.Length-1];
        for(int i=1;i<cuts.Length;i++)
        {
            double lo=cuts[i-1],hi=cuts[i],flo=Evaluate(c,lo),fhi=Evaluate(c,hi);
            if(flo==0||fhi==0||Math.Sign(flo)==Math.Sign(fhi))continue;
            crossings[i-1]=true;
            for(int j=0;j<128;j++)
            {
                double mid=lo+(hi-lo)*.5;if(mid==lo||mid==hi)break;
                double f=Evaluate(c,mid);if(f==0){lo=hi=mid;break;}
                if(Math.Sign(f)==Math.Sign(flo)){lo=mid;flo=f;}else hi=mid;
            }
            roots.Add(lo+(hi-lo)*.5);
        }
        for(int i=0;i<cuts.Length;i++)
        {
            double value=Evaluate(c,cuts[i]);
            if(value==0 || Math.Abs(value)<=epsilon
                && !(i>0&&crossings[i-1]) && !(i<crossings.Length&&crossings[i]))roots.Add(cuts[i]);
        }
        var ordered=roots.Order().Distinct().ToArray();
        if(ordered.Zip(ordered.Skip(1)).Any(p=>p.Second-p.First<2e-13))
            throw new InvalidDataException("Polynomial roots cannot be numerically separated.");
        return ordered;
    }
    private static void EnsureCentreSeparated(CubicPlaneCurve c)
    {
        // Bezier convex-hull subdivision is a conservative bound, not sampling.
        // A box excluding the centre proves every point of that subcurve safe.
        void Visit(PlanePoint a,PlanePoint b,PlanePoint d,PlanePoint e,int depth)
        {
            double lowX=Math.Min(Math.Min(a.X,b.X),Math.Min(d.X,e.X));
            double highX=Math.Max(Math.Max(a.X,b.X),Math.Max(d.X,e.X));
            double lowY=Math.Min(Math.Min(a.Y,b.Y),Math.Min(d.Y,e.Y));
            double highY=Math.Max(Math.Max(a.Y,b.Y),Math.Max(d.Y,e.Y));
            if(lowX>PositionTolerance||highX < -PositionTolerance||lowY>PositionTolerance||highY < -PositionTolerance)return;
            if(depth==48||double.Hypot(a.X,a.Y)<=PositionTolerance||double.Hypot(e.X,e.Y)<=PositionTolerance)
                throw new InvalidDataException("Native curve is not numerically separated from winding centre.");
            var ab=.5*(a+b);var bd=.5*(b+d);var de=.5*(d+e);
            var left=.5*(ab+bd);var right=.5*(bd+de);var middle=.5*(left+right);
            Visit(a,ab,left,middle,depth+1);Visit(middle,right,de,e,depth+1);
        }
        Visit(c.D,c.D+(1d/3)*c.C,c.D+(2d/3)*c.C+(1d/3)*c.B,c.At(1),0);
    }
    private static double Evaluate(double[] c,double t){double sum=0;for(int i=c.Length-1;i>=0;i--)sum=sum*t+c[i];return sum;}
    private static double[] Derivative(double[] c)=>c.Skip(1).Select((v,i)=>(i+1)*v).ToArray();
    private static double[] Product(double[] a,double[] b)
    {var c=new double[a.Length+b.Length-1];for(int i=0;i<a.Length;i++)for(int j=0;j<b.Length;j++)c[i+j]+=a[i]*b[j];return c;}
    private static double Sum(IEnumerable<double> values)
    {double result=0,correction=0;foreach(double value in values){double y=value-correction,t=result+y;correction=(t-result)-y;result=t;}return result;}
    private static double Integral(double[] c)=>Sum(c.Select((v,i)=>v/(i+1)));
}
