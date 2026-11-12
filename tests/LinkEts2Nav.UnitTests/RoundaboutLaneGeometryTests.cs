using System.Numerics;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class RoundaboutLaneGeometryTests
{
    private static RoundaboutCurve[] Circle()
        => Enumerable.Range(0,8).Select(i=>
        {
            double a=i*Math.PI/4,b=(i+1)*Math.PI/4,r=i%2==0?10:12.25;
            Vector3 P(double t)=>new((float)(r*Math.Cos(a+(b-a)*t)),0,(float)(-r*Math.Sin(a+(b-a)*t)));
            var points=Enumerable.Range(0,17).Select(j=>P(j/16d)).ToArray();
            double scale=4*Math.Tan((b-a)/4)*r;
            var native=CubicPlaneCurve.Hermite(new(r*Math.Cos(a),r*Math.Sin(a)),new(r*Math.Cos(b),r*Math.Sin(b)),
                new(-scale*Math.Sin(a),scale*Math.Cos(a)),new(-scale*Math.Sin(b),scale*Math.Cos(b)));
            ulong uid=(ulong)i+1,next=(ulong)((i+1)%8)+1;
            return new RoundaboutCurve(new(uid,0),i%2==0?uid:null,false,points,[new(next,0)],uid,next)
            {NativeCurve=native,RoadNormals=i%2==0?null:points.Select(Vector3.Normalize).ToArray()};
        }).ToArray();

    [Fact] public void ContinuousLaneGeometryPassesCircularityWithoutDeletingSeamsOrChangingNativeCurves()
    {
        var curves=Circle();var graph=curves.ToDictionary(c=>c.Key);var ring=curves.Select(c=>c.Key).ToArray();
        var result=RoundaboutLaneGeometry.Build(graph,ring);
        Assert.InRange(result.Info.ReferenceCircularity,0,.85);
        Assert.InRange(result.Info.LaneCircularity,.99,1.001);
        Assert.Equal(4,result.Info.CorrectedRoads);
        Assert.InRange(result.Info.ReferenceSeamsMeters,17.99,18.01);
        Assert.InRange(result.Info.LaneSeamsMeters,0,.0001);
        Assert.Equal(curves.Sum(c=>c.Points.Count),result.Points.Length);
        var found=new RoundaboutBuilder(curves).Find(1,[curves[0].Key],TestContext.Current.CancellationToken);
        Assert.NotNull(found);Assert.Equal(result.Info.LaneCircularity,found.Circularity,10);
        Assert.InRange(double.Hypot(found.DirectionCheck!.Center!.Value.X,found.DirectionCheck.Center.Value.Y),0,1e-8);
        Assert.All(curves,c=>Assert.Same(c.NativeCurve,graph[c.Key].NativeCurve));
    }
    [Fact] public void ArbitraryRingStartAndReverseOrientationGiveSameGeometryQuality()
    {
        var curves=Circle();var graph=curves.ToDictionary(c=>c.Key);var keys=curves.Select(c=>c.Key).ToArray();
        double expected=RoundaboutLaneGeometry.Build(graph,keys).Info.LaneCircularity;
        for(int start=0;start<keys.Length;start++)
            Assert.Equal(expected,RoundaboutLaneGeometry.Build(graph,keys.Skip(start).Concat(keys.Take(start)).ToArray()).Info.LaneCircularity,8);
        var reverse=curves.Reverse().Select(c=>c with{Points=c.Points.Reverse().ToArray(),
            RoadNormals=c.RoadNormals?.Reverse().Select(n=>-n).ToArray()}).ToArray();
        Assert.Equal(expected,RoundaboutLaneGeometry.Build(reverse.ToDictionary(c=>c.Key),reverse.Select(c=>c.Key).ToArray()).Info.LaneCircularity,8);
    }
    [Fact] public void MultipleRoadRunInterpolatesBetweenItsTwoNativeLaneAnchors()
    {
        RoundaboutCurve[] curves=[
            new(new(1,0),1,false,[new(-1,0,2),new(0,0,2)],[]),
            new(new(2,0),null,false,[new(0,0,0),new(10,0,0)],[]){RoadNormals=[Vector3.UnitZ,Vector3.UnitZ]},
            new(new(3,0),null,false,[new(10,0,0),new(30,0,0)],[]){RoadNormals=[Vector3.UnitZ,Vector3.UnitZ]},
            new(new(4,0),4,false,[new(30,0,4),new(31,0,4)],[])];
        var result=RoundaboutLaneGeometry.Build(curves.ToDictionary(c=>c.Key),curves.Select(c=>c.Key).ToArray());
        Assert.Equal(2,result.Info.CorrectedRoads);Assert.Null(result.Info.Uncertainty);
        Assert.Equal(2,result.Points[2].Z,5);Assert.Equal(8d/3,result.Points[3].Z,5);
        Assert.Equal(result.Points[3],result.Points[4]);Assert.Equal(4,result.Points[5].Z,5);
    }
    [Fact] public void MissingBasisRetainsUnmodifiedGeometryInsteadOfGuessingLaneWidths()
    {
        var curves=Circle().Select(c=>c with{RoadNormals=null}).ToArray();
        var result=RoundaboutLaneGeometry.Build(curves.ToDictionary(c=>c.Key),curves.Select(c=>c.Key).ToArray());
        Assert.Equal(0,result.Info.CorrectedRoads);Assert.NotNull(result.Info.Uncertainty);
        Assert.Equal(curves.SelectMany(c=>c.Points),result.Points);
    }
}
