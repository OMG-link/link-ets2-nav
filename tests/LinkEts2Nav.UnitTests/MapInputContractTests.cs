using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class MapInputContractTests
{
    private static (Prefab Prefab,PrefabDescriptor Descriptor) Scene(float length=10,float offset=0)
    {
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new(){Position=Vector3.Zero,InputLines=[0],OutputLines=[]},
                new(){Position=new(0,0,-length),InputLines=[],OutputLines=[0]}],
            NavCurves=[new(){StartPosition=new(offset,0,0),EndPosition=new(offset,0,-length),Length=length,
                StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity}]
        };
        return(new Prefab{Uid=42,Nodes=[new Node{Uid=10,Position=Vector3.Zero},new Node{Uid=20,Position=new(0,0,-length)}]},descriptor);
    }
    [Fact] public void DescriptorPairingMismatchIsAnErrorRatherThanEmptyGeometry()
    {
        var s=Scene();s.Prefab.Nodes.RemoveAt(1);
        Assert.Throws<InvalidDataException>(()=>PrefabPlacement.TryCreate(s.Prefab,s.Descriptor,out _));
        Assert.Throws<InvalidDataException>(()=>PrefabPathGeometry.Build(s.Prefab,s.Descriptor));
        Assert.Throws<InvalidDataException>(()=>PrefabLaneTopology.Build(s.Prefab,s.Descriptor));
    }
    [Fact] public void InvalidOriginIsAnError()
    {
        var s=Scene();typeof(Prefab).GetProperty(nameof(Prefab.Origin))!.SetValue(s.Prefab,(ushort)2);
        Assert.Throws<InvalidDataException>(()=>PrefabPathGeometry.Build(s.Prefab,s.Descriptor));
    }
    [Fact]
    public void DistinctPrefabSlotsCannotReferenceTheSameMapNode()
    {
        var scene = Scene();
        scene.Prefab.Nodes[1] = new Node { Uid = scene.Prefab.Nodes[0].Uid, Position = new(0, 0, -10) };

        Assert.Throws<InvalidDataException>(() => PrefabLaneTopology.Build(scene.Prefab, scene.Descriptor));
        Assert.Throws<InvalidDataException>(() => PrefabPathGeometry.Build(scene.Prefab, scene.Descriptor));
    }
    [Fact] public void PlacementDoesNotRejectShortSpansOrControlResiduals()
    {
        var s=Scene(.25f);s.Prefab.Nodes[1].Position=new(0,0,-2);
        Assert.True(PrefabPlacement.TryCreate(s.Prefab,s.Descriptor,out var placement));
        Assert.Equal(new Vector3(0,0,-.25f),placement.ToWorld(s.Descriptor.Nodes[1].Position));
    }
    [Fact] public void WideLaneEndpointDoesNotNeedToBeWithinTwentyMetersOfControlNode()
    {
        var s=Scene(10,30);
        var edge=Assert.Single(PrefabPathGeometry.Build(s.Prefab,s.Descriptor));
        Assert.Equal(30,Assert.Single(edge.LanePaths).Points[0].X);
    }
    [Fact] public void SampledLengthIsNotConstrainedToNativeHermiteTangentScale()
    {
        var s=Scene();s.Descriptor.NavCurves[0].Length=1;
        var edge=Assert.Single(PrefabPathGeometry.Build(s.Prefab,s.Descriptor));
        Assert.True(Assert.Single(edge.LanePaths).LengthMeters>2);
    }
    [Fact] public void RepeatedCompoundReferenceIsReportedInsteadOfPrunedAsEmpty()
    {
        var map=new TruckLib.ScsMap.Map();var c=Compound.Add(map,Vector3.Zero);
        c.MapItems[c.Uid]=c;
        Assert.Throws<InvalidDataException>(()=>LoadedMapItems.RetainSupplementSigns(c));
    }
}
