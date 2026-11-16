using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class LaneContinuityIndependentTurnTests
{
    [Theory]
    [InlineData(0,1,LaneTurnKind.ForwardRight)]
    [InlineData(0,-1,LaneTurnKind.ForwardLeft)]
    [InlineData(45,1,LaneTurnKind.ForwardRight)]
    [InlineData(45,-1,LaneTurnKind.ForwardLeft)]
    [InlineData(135,1,LaneTurnKind.RearRight)]
    [InlineData(135,-1,LaneTurnKind.RearLeft)]
    [InlineData(179.1,1,LaneTurnKind.RearRight)]
    [InlineData(179.1,-1,LaneTurnKind.RearLeft)]
    [InlineData(180,1,LaneTurnKind.RearRight)]
    [InlineData(180,-1,LaneTurnKind.RearLeft)]
    public void SplitRestoresActualTravelSideForNearParallelAndBackwardBranches(double degrees,int side,LaneTurnKind expected)
    {
        Vector3 tangent=degrees switch
        {
            0=>new(100,0,0),45=>new(100,0,side*100),135=>new(-100,0,side*100),180=>new(-100,0,0),
            _=>new((float)(100*Math.Cos(degrees*Math.PI/180)),0,(float)(side*100*Math.Sin(degrees*Math.PI/180)))
        };
        Vector3 end=degrees switch {0=>new(100,0,side*3),45=>new(100,0,side*100),135=>new(-100,0,side*100),_=>new(-100,0,side*3)};
        var shared=new NativeLaneCurve(0,new(-100,0,0),Vector3.Zero,new(100,0,0),new(100,0,0),[1,2],[]);
        var main=new NativeLaneCurve(1,Vector3.Zero,new(100,0,0),new(100,0,0),new(100,0,0),[],[0]);
        // Start east, move monotonically to the south (right) or north (left),
        // then end west for a rear branch. This is not a reversed merge fixture.
        var branch=new NativeLaneCurve(2,Vector3.Zero,end,new(100,0,0),tangent,[],[0]);
        var point=Assert.Single(LaneContinuity.Analyze(new(42,[shared,main,branch])).Points);
        var decision=Assert.Single(point.Branches,b=>b.CurveIndex==2);
        Assert.Equal(LaneContinuityRole.Newborn,decision.Role);Assert.Equal(expected,decision.Turn);
    }
}
