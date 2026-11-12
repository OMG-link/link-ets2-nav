using LinkEts2Nav.Map;
using System.Numerics;
namespace LinkEts2Nav.UnitTests;

public sealed class RoundaboutDirectionTests
{
    private static CubicPlaneCurve Arc(double start,double sweep)
    {
        double end=start+sweep,k=4*Math.Tan(sweep/4);
        return CubicPlaneCurve.Hermite(new(Math.Cos(start),Math.Sin(start)),new(Math.Cos(end),Math.Sin(end)),
            new(-k*Math.Sin(start),k*Math.Cos(start)),new(-k*Math.Sin(end),k*Math.Cos(end)));
    }
    private static CubicPlaneCurve[] Circle()=>Enumerable.Range(0,4).Select(i=>Arc(i*Math.PI/2,Math.PI/2)).ToArray();
    private static (RoundaboutCurve[] Curves,RoundaboutCurveKey[] Ring) NativeRing(bool left=false,bool bypass=false)
    {
        Vector3 World(PlanePoint p)=>new((float)p.X,0,-(float)p.Y);
        var arcs=Circle();if(left)arcs=arcs.Reverse().Select(c=>c.Reverse()).ToArray();
        var curves=arcs.Select((c,i)=>new RoundaboutCurve(new(1,i),1,left,
            Enumerable.Range(0,9).Select(j=>World(c.At(j/8d))).ToArray(),[new(1,(i+1)%4)]){NativeCurve=c}).ToList();
        var inlet=new CubicPlaneCurve(new(0,0),new(0,0),new(-1,0),new(2,0));
        var outlet=new CubicPlaneCurve(new(0,0),new(0,0),new(-1,0),new(-1,0));
        curves.Add(new(new(1,10),1,left,[World(inlet.D),World(inlet.At(1))],[new(1,0)],11,0){NativeCurve=inlet});
        curves.Add(new(new(1,11),1,left,[World(outlet.D),World(outlet.At(1))],[],0,22){NativeCurve=outlet});
        curves[1]=curves[1] with {Next=[new(1,2),new(1,11)]};
        if(bypass)
        {
            // The opposite half-ring is an internal legal route, not part of the
            // reference circuit. It must still be inspected by the veto.
            var a=arcs[3].Reverse();var b=arcs[2].Reverse();
            curves.Add(new(new(1,20),1,left,[World(a.D),World(a.At(1))],[new(1,21)]){NativeCurve=a});
            curves.Add(new(new(1,21),1,left,[World(b.D),World(b.At(1))],[new(1,11)]){NativeCurve=b});
            curves[4]=curves[4] with {Next=[new(1,0),new(1,20)]};
        }
        return (curves.ToArray(),Enumerable.Range(0,4).Select(i=>new RoundaboutCurveKey(1,i)).ToArray());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualTrafficSideControlsStandardDirection(bool left)
    {
        var (curves,ring)=NativeRing(left);
        var result=RoundaboutDirection.Check(curves.ToDictionary(c=>c.Key),ring,TestContext.Current.CancellationToken);
        Assert.True(result.Complete);Assert.False(result.OppositeWalk);
    }
    [Fact] public void NativeInternalBypassRejectsBeforePrefabIsConfirmed()
    {
        var (curves,ring)=NativeRing(bypass:true);
        var result=RoundaboutDirection.Check(curves.ToDictionary(c=>c.Key),ring,TestContext.Current.CancellationToken);
        Assert.True(result.Complete);Assert.True(result.OppositeWalk);
        Assert.Null(new RoundaboutBuilder(curves).Find(1,[new(1,10)],TestContext.Current.CancellationToken));
    }
    [Fact] public void WrongTrafficSideRejectsAndTwoPrefabInternalPortsAreExcluded()
    {
        var (curves,ring)=NativeRing();
        var wrong=curves.Select(c=>c with{LeftHandTraffic=true}).ToDictionary(c=>c.Key);
        Assert.True(RoundaboutDirection.Check(wrong,ring,TestContext.Current.CancellationToken).OppositeWalk);
        RoundaboutCurveKey Remap(RoundaboutCurveKey key)=>key.CurveIndex is 2 or 3?new(2,key.CurveIndex):key;
        var combined=curves.Select(c=>c with{Key=Remap(c.Key),PrefabUid=Remap(c.Key).ItemUid,
            Next=c.Next.Select(Remap).ToArray(),EntryLane=0,ExitLane=0,
            EntryNode=c.Key.CurveIndex==2?99:c.Key.CurveIndex==0?100:c.EntryNode,
            ExitNode=c.Key.CurveIndex==1?99:c.Key.CurveIndex==3?100:c.ExitNode}).ToDictionary(c=>c.Key);
        var result=RoundaboutDirection.Check(combined,ring.Select(Remap).ToArray(),TestContext.Current.CancellationToken);
        Assert.True(result.Complete);Assert.False(result.OppositeWalk);Assert.Equal(1,result.K);
    }
    [Fact] public void ProvenForbiddenTrafficIsDifferentFromUnknownTraffic()
    {
        var (curves,ring)=NativeRing();
        var forbidden=curves[0] with{Key=new(1,99),ProvenTraffic=false,TrafficKnown=true};
        var known=RoundaboutDirection.Check(curves.Append(forbidden).ToDictionary(c=>c.Key),ring,TestContext.Current.CancellationToken);
        var unknown=RoundaboutDirection.Check(curves.Append(forbidden with{TrafficKnown=false}).ToDictionary(c=>c.Key),ring,TestContext.Current.CancellationToken);
        Assert.True(known.Complete);Assert.False(unknown.Complete);
    }
    [Fact] public void NativeAreaCentroidIsTranslationAndOrientationInvariant()
    {
        var circle=Circle();var origin=new PlanePoint(1e6,-2e6);
        var shifted=circle.Select(c=>c with{D=c.D+origin}).ToArray();
        var a=CubicWinding.Centroid(shifted);var b=CubicWinding.Centroid(shifted.Reverse().Select(c=>c.Reverse()).ToArray());
        Assert.InRange(double.Hypot(a.X-origin.X,a.Y-origin.Y),0,1e-8);
        Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,1e-8);
    }
    [Fact] public void NativeAnglesPreserveFullTurnsInsteadOfEndpointShortAngle()
    {
        var arcs=Circle();double rotation=CubicWinding.Rotation(arcs);
        var angles=arcs.Select(c=>CubicWinding.Angle(c.Rotate(rotation))).ToArray();
        Assert.Equal(2*Math.PI,angles.Sum(),10);
        Assert.Equal(1.5*Math.PI,angles.Take(3).Sum(),10);
        Assert.Equal(-Math.PI/2,CubicWinding.Angle(arcs[3].Reverse().Rotate(rotation)),10);
    }
    [Theory]
    [InlineData(0,1,-1,.25)] // quadratic tangency
    [InlineData(1,-1.5,.75,-.125)] // cubic stationary crossing
    [InlineData(0,0,1,-.5)] // linear crossing
    public void BranchCutUsesSideSignsIncludingStationaryRoots(double a,double b,double c,double d)
    {
        var curve=new CubicPlaneCurve(new(0,a),new(0,b),new(0,c),new(-1,d));
        double expected=a==0&&b==1?0:Math.Atan2(curve.At(1).Y,-1)-Math.Atan2(d,-1)-2*Math.PI;
        Assert.Equal(expected,CubicWinding.Angle(curve),10);
    }
    [Fact] public void CentreCrossingIsIndeterminate()
    {
        var line=new CubicPlaneCurve(new(0,0),new(0,0),new(2,2),new(-1,-1));
        Assert.Throws<InvalidDataException>(()=>CubicWinding.Angle(line));
    }
    [Theory]
    [InlineData(.0001)]
    [InlineData(.00001)]
    public void CloselySpacedCrossingsDoNotLoseTheirParity(double separation)
    {
        // (t-.5) * ((t-.5)^2 - delta^2): three roots, all on x=-1.
        var curve=new CubicPlaneCurve(new(0,1),new(0,-1.5),new(0,.75-separation*separation),new(-1,-.125+.5*separation*separation));
        double expected=Math.Atan2(curve.At(1).Y,-1)-Math.Atan2(curve.D.Y,-1)-2*Math.PI;
        Assert.Equal(expected,CubicWinding.Angle(curve),9);
    }
    [Fact] public void DegenerateAreaIsIndeterminate()
        => Assert.Throws<InvalidDataException>(()=>CubicWinding.Centroid([new(new(0,0),new(0,0),new(1,0),new(0,0))]));
    [Fact] public void DifferentInterfaceNegativeWalkRejectsButSameInterfaceDoesNot()
    {
        var edges=new AngularEdge[]{new(0,1,-1)};
        Assert.True(DistinctPortWalk.Check(2,edges,[new(0,10)],[new(1,20)],TestContext.Current.CancellationToken).OppositeWalk);
        Assert.False(DistinctPortWalk.Check(2,edges,[new(0,10)],[new(1,10)],TestContext.Current.CancellationToken).OppositeWalk);
    }
    [Fact] public void NegativeCycleRequiresDistinctInterfaceReachability()
    {
        var edges=new AngularEdge[]{new(0,1,10),new(1,1,-.1),new(1,2,10),new(3,3,-1)};
        Assert.True(DistinctPortWalk.Check(4,edges,[new(0,10)],[new(2,20)],TestContext.Current.CancellationToken).NegativeCycle);
        Assert.False(DistinctPortWalk.Check(4,edges,[new(0,10)],[new(2,10)],TestContext.Current.CancellationToken).OppositeWalk);
    }
    [Fact] public void ArbitrarilySmallUsefulNegativeCycleStillDefeatsAnyFinitePrefix()
        => Assert.True(DistinctPortWalk.Check(1,[new(0,0,-CubicWinding.AngleTolerance/2)],
            [new(0,10)],[new(0,20)],TestContext.Current.CancellationToken).NegativeCycle);
    [Fact] public void TwoSourcesHandleExitExclusionAndLocalBacktrackingIsAllowed()
    {
        var edges=new AngularEdge[]{new(0,2,-5),new(1,2,1),new(2,3,2)};
        Assert.False(DistinctPortWalk.Check(4,edges,[new(0,10),new(1,20)],[new(3,10)],TestContext.Current.CancellationToken).OppositeWalk);
        Assert.True(DistinctPortWalk.Check(4,edges,[new(0,10),new(1,20)],[new(3,30)],TestContext.Current.CancellationToken).OppositeWalk);
    }
    [Fact] public void TwoLabelSolverMatchesEnumeratedSourcesOnSmallGraphs()
    {
        // Independent Floyd-Warshall oracle includes paths, relevant negative
        // cycles, disconnected regions, same-interface exits and several sources.
        var random=new Random(1729);
        for(int sample=0;sample<150;sample++)
        {
            const int n=7;var edges=new List<AngularEdge>();
            var distances=new double[n,n];
            for(int i=0;i<n;i++)for(int j=0;j<n;j++)distances[i,j]=i==j?0:double.PositiveInfinity;
            for(int i=0;i<n;i++)for(int j=0;j<n;j++)if(random.Next(4)==0)
            {double weight=random.Next(-3,7);edges.Add(new(i,j,weight));distances[i,j]=Math.Min(distances[i,j],weight);}
            for(int k=0;k<n;k++)for(int i=0;i<n;i++)for(int j=0;j<n;j++)
                distances[i,j]=Math.Min(distances[i,j],distances[i,k]+distances[k,j]);
            var entries=new AngularPort[]{new(0,10),new(1,20),new(2,30)};
            var exits=new AngularPort[]{new(4,10),new(5,20),new(6,30)};
            bool expected=entries.Any(a=>exits.Any(b=>a.Interface!=b.Interface&&
                (distances[a.Node,b.Node]<0||Enumerable.Range(0,n).Any(v=>double.IsFinite(distances[a.Node,v])&&distances[v,v]<0&&double.IsFinite(distances[v,b.Node])))));
            Assert.Equal(expected,DistinctPortWalk.Check(n,edges,entries,exits,TestContext.Current.CancellationToken).OppositeWalk);
        }
    }
}

