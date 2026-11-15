using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneMergeOriginTests
{
    private static PrefabLanePath Lane(ulong prefab,ulong entry,ulong exit,int input,int output,double length=20)
        =>new(prefab,entry,exit,input,output,input,output,2,2,[input],[Vector3.Zero,new(0,0,(float)length)],length);
    private static DirectedRoad Edge(string id,params PrefabLanePath[] paths)
        =>new(id,paths[0].EntryNodeUid,paths[0].ExitNodeUid,paths[0].Points){LanePaths=paths};
    private static DirectedRoad Road(double length)=>new("road",30,10,[Vector3.Zero,new(0,0,(float)length)]);

    [Fact]
    public void NearestJoinSameEntranceStopsBeforeAnyOlderDifferentSources()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);var other=Lane(42,11,20,0,1);
        var oldSide=Lane(43,100,30,0,1);var oldMain=Lane(43,200,30,0,0);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),Edge("prefab:42:2:1",other),Road(100),
            Edge("prefab:43:0:1",oldSide),Edge("prefab:43:2:1",oldMain)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,10,main,10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);Assert.Equal(42UL,result.SourcePrefab);
        Assert.Equal(10UL,result.EndingEntrance);Assert.Equal(10UL,result.MainEntrance);Assert.Equal(0,result.BackwardMeters);
    }

    [Theory]
    [InlineData(479, LaneMergeKind.Ramp)]
    [InlineData(480, LaneMergeKind.Ramp)]
    [InlineData(480.01, LaneMergeKind.LaneReduction)]
    public void PreviousDifferentEntrancesAreUsedOnlyWithinTheFiveHundredMetreWindow(double roadLength,LaneMergeKind expected)
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var side=Lane(43,100,30,0,1);var kept=Lane(43,200,30,0,0);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),Road(roadLength),Edge("prefab:43:0:1",side),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(expected,result.Kind);
        if(expected==LaneMergeKind.Ramp){Assert.Equal(100UL,result.EndingEntrance);Assert.Equal(200UL,result.MainEntrance);}
    }

    [Fact]
    public void MissingOneSourceIsUnknownInsteadOfAssumingSameOrigin()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var kept=Lane(43,200,30,0,0);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);Assert.Contains("未确认",result.Reason);
    }

    [Fact]
    public void InheritedLaneLampCommandsDoNotTerminateSourceTracing()
    {
        var ending=Lane(42,10,20,1,0) with{CurveBlinkers=[LaneBlinker.Right]};
        var main=Lane(42,10,20,0,0) with{CurveBlinkers=[LaneBlinker.Left]};
        var side=Lane(43,100,30,0,1);var kept=Lane(43,200,30,0,0);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:0:1",side),Edge("prefab:43:2:1",kept)]);
        Assert.Equal(LaneMergeKind.Ramp,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }
    [Fact]
    public void MultipleLaneSlotsWithinOneSourceEntranceStillHaveAnUnambiguousDirection()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var sideA=Lane(43,100,30,0,1);var sideB=Lane(43,100,30,1,1);var kept=Lane(43,200,30,0,0);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:0:1",sideA,sideB),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.Ramp,result.Kind);Assert.Equal(100UL,result.EndingEntrance);Assert.Equal(200UL,result.MainEntrance);
    }
}
