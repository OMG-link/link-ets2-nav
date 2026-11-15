using System.Numerics;using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class LaneMergeSourceAlternativesTests
{
    static PrefabLanePath Lane(ulong p,ulong entry,ulong exit,int input,int output,int curve,double length)
        =>new(p,entry,exit,input,output,input,output,2,2,[curve],[Vector3.Zero,new(0,0,(float)length)],length);
    static DirectedRoad Edge(string id,params PrefabLanePath[] paths)=>new(id,paths[0].EntryNodeUid,paths[0].ExitNodeUid,paths[0].Points){LanePaths=paths};
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternativeNativeCurvesWithinTheSameLaneSlotMustRetainBudgetUncertainty(bool reverse)
    {
        var ending=Lane(42,10,20,1,0,1,20);var main=Lane(42,10,20,0,0,0,20);
        var shorter=Lane(43,100,30,0,1,5,10);var longer=shorter with{CurveIndices=[6,7],Points=[Vector3.Zero,new(0,0,40)],LengthMeters=40};
        var kept=Lane(43,200,30,0,0,8,10);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),new("road",30,10,[Vector3.Zero,new(0,0,460)]),
            Edge("prefab:43:0:1",reverse?[longer,shorter]:[shorter,longer]),Edge("prefab:43:2:1",kept)]);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SameInputAlternativeCurvesRemainUncertainWhenTheAccumulatedWindowIsCrossedLater(bool reverse)
    {
        var ending=Lane(42,10,20,1,0,1,20);var main=Lane(42,10,20,0,0,0,20);
        var shorter=Lane(43,100,30,0,1,5,10);var longer=shorter with{CurveIndices=[6,7],Points=[Vector3.Zero,new(0,0,40)],LengthMeters=40};
        var kept=Lane(43,100,30,1,0,8,10);
        var farA=Lane(44,300,50,0,0,10,20);var farB=Lane(44,400,50,0,1,11,20);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),new("road",30,10,[Vector3.Zero,new(0,0,100)]),
            Edge("prefab:43:0:1",reverse?[longer,shorter,kept]:[shorter,longer,kept]),
            new("second-road",50,100,[Vector3.Zero,new(0,0,330)]),Edge("prefab:44:0:1",farA),Edge("prefab:44:2:1",farB)]);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SameInputAlternativesStraddlingTheCurrentStepAreAlsoUnknown(bool reverse)
    {
        var ending=Lane(42,10,20,1,0,1,20);var main=Lane(42,10,20,0,0,0,20);
        var shorter=Lane(43,100,30,0,1,5,10);var longer=shorter with{CurveIndices=[6,7],Points=[Vector3.Zero,new(0,0,40)],LengthMeters=40};
        var kept=Lane(43,100,30,1,0,8,10);
        var graph=new NativeLaneGraph([Edge("prefab:42:0:1",ending,main),new("road",30,10,[Vector3.Zero,new(0,0,460)]),
            Edge("prefab:43:0:1",reverse?[longer,shorter,kept]:[shorter,longer,kept])]);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }
}
