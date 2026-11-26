using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class PrefabCurveSpanTests
{
    [Theory]
    [InlineData(3,1000f,"2500 m")]
    [InlineData(8193,0.001f,"8192 queue items")]
    public void SearchBudgetReportsOnlyAnUnresolvedReachableTarget(int count,float length,string limit)
    {
        var curves=Enumerable.Range(0,count+1).Select(_=>new NavCurve{Length=length}).ToList();
        for(int index=0;index<count-1;index++)
        {
            curves[index].NextLines.Add(index+1);
            curves[index+1].PreviousLines.Add(index);
        }
        var descriptor=new PrefabDescriptor{NavCurves=curves};
        Assert.Empty(PrefabCurvePathfinder.FindEach(descriptor,0,new HashSet<int>{count}));
        var error=Assert.Throws<NotSupportedException>(()=>
            PrefabCurvePathfinder.FindEach(descriptor,0,new HashSet<int>{count-1},42));
        Assert.Contains("Prefab 000000000000002A",error.Message);
        Assert.Contains(limit,error.Message);
    }

    [Fact]
    public void NativeSuccessorRemainsReachableAcrossCurveEndpointGap()
    {
        var descriptor = new PrefabDescriptor
        {
            NavCurves =
            [
                new() { StartPosition = Vector3.Zero, EndPosition = new(0, 0, -10), Length = 10, NextLines = { 1 } },
                new() { StartPosition = new(0, 0, -11), EndPosition = new(0, 0, -21), Length = 10 }
            ]
        };
        var targets = new HashSet<int> { 1 };
        int[] expected = [0, 1];

        Assert.Equal(expected, PrefabCurvePathfinder.Find(descriptor, [0], targets));
        Assert.Equal(expected, PrefabCurvePathfinder.FindEach(descriptor, 0, targets)[1]);
    }

    [Fact]
    public void NativeCurveIndicesReferToTheActualRepresentativePolylineAfterPlacementAndGrading()
    {
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new(){Position=Vector3.Zero,InputLines=[0],OutputLines=[]},
                new(){Position=new(0,0,-100),InputLines=[],OutputLines=[1]}],
            NavCurves=[new(){StartPosition=Vector3.Zero,EndPosition=new(0,0,-50),Length=50,
                    StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity,NextLines={1}},
                new(){StartPosition=new(0,0,-50),EndPosition=new(0,0,-100),Length=50,
                    StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity}]
        };
        var prefab=new Prefab{Uid=1,Nodes=[new Node{Uid=10,Position=new(1000,10,1000)},new Node{Uid=20,Position=new(1000,20,900)}]};
        var path=Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.Equal(new[]{0,1},path.NativeCurveSpans.Select(p=>p.CurveIndex));
        Assert.Equal(0,path.NativeCurveSpans[0].PointIndex);
        var second=path.Points[path.NativeCurveSpans[1].PointIndex];
        Assert.Equal(1000,second.X,3);Assert.Equal(950,second.Z,3);Assert.Equal(15,second.Y,3);
        Assert.Equal(prefab.Nodes[0].Position,path.Points[0]);
    }
}
