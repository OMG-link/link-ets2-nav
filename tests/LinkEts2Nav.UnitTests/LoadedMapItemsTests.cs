using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class LoadedMapItemsTests
{
    [Fact]
    public void NestedSignsKeepWorldGeometryAndTopLevelItemsWinDuplicateUids()
    {
        var map=new TruckLib.ScsMap.Map();var outer=Compound.Add(map,new(100,5,200));
        var inner=Compound.Add(outer,new(120,5,220));var sign=Sign.Add(inner,new(125,6,223),"town");
        var decoration=Model.Add(inner,new(130,0,230),"decoration",default,default);decoration.ModelHookups=false;
        sign.Node.Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,.4f);
        var position=sign.Node.Position;var rotation=sign.Node.Rotation;var owner=sign.Node.Parent;
        // A host can already have promoted a child. Enumerating it again must not duplicate it.
        map.MapItems[sign.Uid]=sign;
        var items=LoadedMapItems.Enumerate(map.MapItems.Values).ToArray();
        Assert.Single(items,i=>i.Uid==sign.Uid);Assert.Same(sign,items.Single(i=>i.Uid==sign.Uid));
        Assert.Equal(position,sign.Node.Position);Assert.Equal(rotation,sign.Node.Rotation);Assert.Same(owner,sign.Node.Parent);
        Assert.Equal(4,items.Length);Assert.Same(decoration,inner.MapItems[decoration.Uid]);
    }

    [Fact]
    public void PruningPrivateSupplementKeepsOnlyRequiredChildrenAndTheirOriginalNodes()
    {
        var map=new TruckLib.ScsMap.Map();var compound=Compound.Add(map,Vector3.Zero);
        var nested=Compound.Add(compound,new(10,0,20));var sign=Sign.Add(nested,new(12,1,23),"town");
        var decoration=Model.Add(compound,new(20,0,30),"decoration",default,default);decoration.ModelHookups=true;
        var enabledDecoration=Model.Add(compound,new(30,0,40),"enabled",default,default);enabledDecoration.ModelHookups=true;
        var position=sign.Node.Position;var owner=sign.Node.Parent;
        Assert.True(LoadedMapItems.RetainSupplementSigns(compound));
        Assert.DoesNotContain(decoration.Uid,compound.MapItems.Keys);Assert.DoesNotContain(decoration.Node.Uid,compound.Nodes.Keys);
        Assert.DoesNotContain(enabledDecoration.Uid,compound.MapItems.Keys);Assert.Same(sign,nested.MapItems[sign.Uid]);
        Assert.Equal(position,sign.Node.Position);Assert.Same(owner,sign.Node.Parent);
        Assert.Same(sign.Node,nested.Nodes[sign.Node.Uid]);
    }

    [Fact]
    public void EmptyDecorationContainersAreNotRetained()
    {
        var map=new TruckLib.ScsMap.Map();var compound=Compound.Add(map,Vector3.Zero);
        var decoration=Model.Add(compound,Vector3.One,"decoration",default,default);decoration.ModelHookups=false;
        Assert.False(LoadedMapItems.RetainSupplementSigns(compound));Assert.Empty(compound.MapItems);Assert.Empty(compound.Nodes);
    }
}
