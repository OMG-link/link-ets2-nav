using System.Numerics;
using System.Reflection;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
// Reverse outlet tracing restores only geometric boundaries; passage ownership is built forward.
public sealed class ComponentRestoreOwnershipTests
{
    private static readonly DirectedRoad Edge=new("prefab:2A:1:2",1,2,[Vector3.Zero,new(0,0,100)]);
    private static ComponentBoundary? Restore(ComponentBoundary? point)
        =>(ComponentBoundary?)typeof(ComponentContinuityIndex).GetMethod("Restore",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[point,new Dictionary<NativeRoadKey,DirectedRoad>{{NativeRoadKey.Of(Edge),Edge}}]);
    [Fact]
    public void UnknownBoundaryCannotBorrowAnUnrelatedVisit()
    {
        var missing=new ComponentBoundary("missing",20,Vector3.Zero){StartNodeUid=2,EndNodeUid=1};
        var wrapped=Assert.Throws<TargetInvocationException>(()=>Restore(missing));
        Assert.IsType<KeyNotFoundException>(wrapped.InnerException);
    }
    [Fact]
    public void MissingBoundaryRemainsAbsent()=>Assert.Null(Restore(null));
    [Theory][InlineData(ComponentBoundaryInterface.Start,ComponentBoundaryInterface.End)]
    [InlineData(ComponentBoundaryInterface.End,ComponentBoundaryInterface.Start)]
    [InlineData(ComponentBoundaryInterface.None,ComponentBoundaryInterface.None)]
    public void ReversedBoundaryKeepsItsExactVisitAndRestoresForwardMileage(ComponentBoundaryInterface input,ComponentBoundaryInterface output)
    {
        var point=new ComponentBoundary(Edge.Id,20,new(0,0,80)){StartNodeUid=2,EndNodeUid=1,Interface=input};
        var actual=Restore(point)!;
        Assert.Equal(80,actual.OffsetMeters);Assert.Equal(point.Position,actual.Position);
        Assert.Equal(Edge.Id,actual.EdgeId);Assert.Equal(1UL,actual.StartNodeUid);Assert.Equal(2UL,actual.EndNodeUid);
        Assert.Equal(output,actual.Interface);
    }
}
