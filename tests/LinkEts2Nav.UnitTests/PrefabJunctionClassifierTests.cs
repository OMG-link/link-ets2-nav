using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using System.Numerics;
namespace LinkEts2Nav.UnitTests;
public sealed class PrefabJunctionClassifierTests
{
    private static PrefabDescriptor Crossing(uint flags=32)=>new()
    {
        Nodes=[new ControlNode{InputLines=[0],OutputLines=[]},new ControlNode{InputLines=[1],OutputLines=[]},
            new ControlNode{InputLines=[],OutputLines=[0]},new ControlNode{InputLines=[],OutputLines=[1]}],
        NavCurves=[new NavCurve(),new NavCurve()],
        Intersections=[new Intersection{CurveId=0,Flags=new(flags)},new Intersection{CurveId=1,Flags=new(0)}]
    };
    [Theory]
    [InlineData(32u,true)]
    [InlineData(0x40020u,true)]
    [InlineData(0x10020u,false)]
    [InlineData(0x20020u,false)]
    public void NativeCrossingQualifiesButSplitAndMergeDoNot(uint flags,bool expected)
    {
        var result=PrefabJunctionClassifier.Analyze(Crossing(flags));
        Assert.Equal(2,result.EntranceCount);Assert.Equal(2,result.ExitCount);
        Assert.Equal(expected,result.IsJunction);
    }
    [Fact]
    public void CountsControlInterfacesInsteadOfLaneSlots()
    {
        var d=Crossing();d.Nodes[0].InputLines=[0,1];d.Nodes[1].InputLines=[];
        Assert.Equal(1,PrefabJunctionClassifier.Analyze(d).EntranceCount);
        Assert.False(PrefabJunctionClassifier.Analyze(d).IsJunction);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossingWithinOneEntranceOrOneExitIsNotAnIntersection(bool sameEntrance)
    {
        var d=Crossing();d.NavCurves.Add(new NavCurve());
        if(sameEntrance){d.Nodes[0].InputLines=[0,1];d.Nodes[1].InputLines=[2];d.Nodes[3].OutputLines=[1,2];}
        else{d.Nodes[1].InputLines=[1,2];d.Nodes[2].OutputLines=[0,1];d.Nodes[3].OutputLines=[2];}
        var info=PrefabJunctionClassifier.Analyze(d);
        Assert.Equal(2,info.EntranceCount);Assert.Equal(2,info.ExitCount);Assert.False(info.IsJunction);
    }
    [Fact]
    public void MissingOrIncompleteCrossingDataDoesNotGuess()
    {
        var d=Crossing();d.Intersections.Clear();Assert.False(PrefabJunctionClassifier.Analyze(d).IsJunction);
        d=Crossing(48);Assert.False(PrefabJunctionClassifier.Analyze(d).IsJunction);
    }

    [Fact]
    public void UncrossedForkAndMergeAreRampJunctionsButTwoInterfaceRoadIsNot()
    {
        var d=new PrefabDescriptor{NavCurves=[new(),new()],Nodes=[new(){InputLines=[0,1],OutputLines=[]},
            new(){OutputLines=[0],InputLines=[]},new(){OutputLines=[1],InputLines=[]}]};
        var fork=PrefabJunctionClassifier.Analyze(d);
        Assert.False(fork.IsJunction);Assert.True(fork.IsRampJunction);Assert.True(fork.IsAnyJunction);
        d.Nodes=[new(){InputLines=[0],OutputLines=[]},new(){InputLines=[1],OutputLines=[]},new(){OutputLines=[0,1],InputLines=[]}];
        Assert.True(PrefabJunctionClassifier.Analyze(d).IsRampJunction);
        d.Nodes=[new(){InputLines=[0],OutputLines=[1]},new(){InputLines=[1],OutputLines=[0]}];
        Assert.False(PrefabJunctionClassifier.Analyze(d).IsAnyJunction);
    }

    [Fact]
    public void MinorPassageUsesAllLaneCentersAndRequiresBothChosenInterfacesToBeWide()
    {
        var d=new PrefabDescriptor{NavCurves=[
            new(){StartPosition=new(-1.5f,0,0),EndPosition=new(-1.5f,0,100)},
            new(){StartPosition=new(1.5f,0,100),EndPosition=new(1.5f,0,0)},
            new(){StartPosition=new(10,0,40),EndPosition=new(10,0,60)}],
            Nodes=[new(){InputLines=[0],OutputLines=[1]},new(){InputLines=[1],OutputLines=[0]},
                new(){InputLines=[2],OutputLines=[]},new(){OutputLines=[2],InputLines=[]}]};
        var info=PrefabJunctionClassifier.Analyze(d);
        Assert.Equal(new[]{3d,3d,0d,0d},info.InterfaceWidths);
        Assert.True(info.IsMinorPassage(0,1));
        Assert.False(info.IsMinorPassage(2,3));
        Assert.False(info.IsMinorPassage(0,2));
        d.NavCurves[1].EndPosition=new(1.49f,0,0);
        Assert.False(PrefabJunctionClassifier.Analyze(d).IsMinorPassage(0,1));
    }

    [Fact]
    public void ElevationDifferenceDoesNotWidenAnInterface()
    {
        var d=new PrefabDescriptor{NavCurves=[new(){StartPosition=Vector3.Zero},new(){StartPosition=new(0,20,1.5f)}],
            Nodes=[new(){InputLines=[0,1],OutputLines=[]}]};
        Assert.Equal(1.5,Assert.Single(PrefabJunctionClassifier.Analyze(d).InterfaceWidths));
    }
}
