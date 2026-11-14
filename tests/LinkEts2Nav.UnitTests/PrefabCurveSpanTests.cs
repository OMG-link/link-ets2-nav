using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class PrefabCurveSpanTests
{
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
        Assert.Equal(1,path.NativeCurveSpans[0].PointIndex);
        var second=path.Points[path.NativeCurveSpans[1].PointIndex];
        Assert.Equal(1000,second.X,3);Assert.Equal(950,second.Z,3);Assert.Equal(15,second.Y,3);
        Assert.Equal(prefab.Nodes[0].Position,path.Points[0]);
    }
}
