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
    public void SameEntranceContinuesToAnOlderDifferentSourceWithinTheSingleBudget()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);var other=Lane(42,11,20,0,1);
        var oldSide=Lane(43,100,30,0,1);var oldMain=Lane(43,200,30,0,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Edge("prefab:42:2:1",other),Road(100),
            Edge("prefab:43:0:1",oldSide),Edge("prefab:43:2:1",oldMain)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,10,main,10);
        Assert.Equal(LaneMergeKind.Ramp,result.Kind);Assert.Equal(43UL,result.SourcePrefab);
        Assert.Equal(100UL,result.EndingEntrance);Assert.Equal(200UL,result.MainEntrance);
        Assert.Equal(130,result.BackwardMeters);Assert.Equal(2,result.Steps.Count);
    }

    [Theory]
    [InlineData(459, LaneMergeKind.Ramp)]
    [InlineData(460, LaneMergeKind.Ramp)]
    [InlineData(460.01, LaneMergeKind.LaneReduction)]
    public void PreviousDifferentEntrancesAreUsedOnlyWithinTheFiveHundredMetreWindow(double roadLength,LaneMergeKind expected)
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var side=Lane(43,100,30,0,1);var kept=Lane(43,200,30,0,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(roadLength),Edge("prefab:43:0:1",side),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(expected,result.Kind);
        if(expected==LaneMergeKind.Ramp){Assert.Equal(100UL,result.EndingEntrance);Assert.Equal(200UL,result.MainEntrance);}
    }

    [Fact]
    public void MissingOneSourceIsUnknownInsteadOfAssumingSameOrigin()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var kept=Lane(43,200,30,0,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);Assert.Contains("未确认",result.Reason);
    }

    [Fact]
    public void InheritedLaneLampCommandsDoNotTerminateSourceTracing()
    {
        var ending=Lane(42,10,20,1,0) with{CurveBlinkers=[LaneBlinker.Right]};
        var main=Lane(42,10,20,0,0) with{CurveBlinkers=[LaneBlinker.Left]};
        var side=Lane(43,100,30,0,1);var kept=Lane(43,200,30,0,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:0:1",side),Edge("prefab:43:2:1",kept)]);
        Assert.Equal(LaneMergeKind.Ramp,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }
    [Fact]
    public void MultipleLaneSlotsWithinOneSourceEntranceStillHaveAnUnambiguousDirection()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var sideA=Lane(43,100,30,0,1);var sideB=Lane(43,100,30,1,1);var kept=Lane(43,200,30,0,0);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(100),Edge("prefab:43:0:1",sideA,sideB),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.Ramp,result.Kind);Assert.Equal(100UL,result.EndingEntrance);Assert.Equal(200UL,result.MainEntrance);
    }

    [Fact]
    public void AnUnloadedRoadAfterASameInputDoesNotCountAsACompletedWindow()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var a=Lane(43,100,30,1,1);var b=Lane(43,100,30,0,0);var unrelated=Lane(43,200,30,0,2);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(100),
            Edge("prefab:43:0:1",a,b),Edge("prefab:43:2:1",unrelated)]);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }

    [Fact]
    public void RecursionDoesNotRestartFiveHundredMetresAtEachPrefab()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var a=Lane(43,100,30,1,1);var b=Lane(43,100,30,0,0);
        var farA=Lane(44,300,200,0,1);var farB=Lane(44,400,200,0,0);
        var fartherRoad=new DirectedRoad("second-road",200,100,[Vector3.Zero,new(0,0,300)]);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(200),
            Edge("prefab:43:0:1",a,b),fartherRoad,Edge("prefab:44:0:1",farA),Edge("prefab:44:2:1",farB)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);Assert.Equal(500,result.BackwardMeters);
        Assert.DoesNotContain(result.Steps,s=>s.PrefabUid==44);
    }

    [Fact]
    public void OlderSameInputRecursionDoesNotReplaceTheNearestSourceApproachAOrItsLaneGroup()
    {
        PrefabLanePath P(ulong prefab,ulong entry,ulong exit,int input,int output,int inputs,int outputs)=>
            Lane(prefab,entry,exit,input,output) with{EntryLaneCount=inputs,ExitLaneCount=outputs};
        var a=P(42,10,20,3,2,4,3);var b=P(42,10,20,2,2,4,3);
        var all=new[]{P(42,10,20,0,0,4,3),P(42,10,20,1,1,4,3),a,b};
        var near=new[]{P(43,100,30,0,1,3,4),P(43,100,30,1,2,3,4),P(43,100,30,2,3,3,4)};
        var other=P(43,200,30,0,0,1,4);
        var older=new[]{P(44,300,50,0,1,2,3),P(44,300,50,1,2,2,3)};
        var olderOther=P(44,400,50,0,0,1,3);
        DirectedRoad[] graph=[Edge("prefab:42:0:1",all),Road(50),Edge("prefab:43:0:1",near),Edge("prefab:43:2:1",other),
            new("middle",50,100,[Vector3.Zero,new(0,0,50)]),Edge("prefab:44:0:1",older),Edge("prefab:44:2:1",olderOther),
            new("long",500,300,[Vector3.Zero,new(0,0,600)])];
        var result=new LaneMergeOriginClassifier(SourceGeometryFixture.Graph(graph)).Classify(a,20,b,20);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);
        Assert.Equal(100UL,result.SourceEntrance);Assert.Equal(43UL,result.ApplicabilitySourcePrefab);
        Assert.Equal(new[]{1,2,3},result.ApplicableEntryLanes);
        Assert.Contains(result.Steps,s=>s.PrefabUid==44&&s.EndingEntrance==300);
    }

    [Fact]
    public void AmbiguousSourceLaneLengthsStraddlingTheBudgetCannotPretendToBeInOrOutsideIt()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var shorter=Lane(43,100,30,0,1,10);var longer=Lane(43,100,30,1,1,40);var kept=Lane(43,200,30,0,0,10);
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),Road(460),
            Edge("prefab:43:0:1",shorter,longer),Edge("prefab:43:2:1",kept)]);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20).Kind);
    }

    [Fact]
    public void TheOppositeDirectionOfTheSameRoadIsNotAnUpstreamSourceOrAKnownCycle()
    {
        var ending=Lane(42,10,20,1,0);var main=Lane(42,10,20,0,0);
        var side=Lane(43,100,30,0,1);var kept=Lane(43,200,30,0,0);var forward=Road(100);
        var reverse=forward with{StartNodeUid=forward.EndNodeUid,EndNodeUid=forward.StartNodeUid,Points=forward.Points.Reverse().ToArray()};
        var graph=SourceGeometryFixture.Graph([Edge("prefab:42:0:1",ending,main),forward,reverse,
            Edge("prefab:43:0:1",side),Edge("prefab:43:2:1",kept)]);
        var result=new LaneMergeOriginClassifier(graph).Classify(ending,20,main,20);
        Assert.Equal(LaneMergeKind.Ramp,result.Kind);Assert.Equal(140,result.BackwardMeters);
    }
}

// These fixtures isolate entrance/slot provenance and the cumulative distance
// budget. They explicitly declare independent native chains without local lane
// birth/death; actual competing merge qualification has separate tests.
internal static class SourceGeometryFixture
{
    public static NativeLaneGraph Graph(IReadOnlyList<DirectedRoad> source)
    {
        var replacements=new Dictionary<PrefabLanePath,PrefabLanePath>(ReferenceEqualityComparer.Instance);
        foreach(var family in source.SelectMany(e=>e.LanePaths).GroupBy(p=>p.PrefabUid))
        {
            var curves=new List<NativeLaneCurve>();var paths=new List<PrefabLanePath>();
            foreach(var path in family)
            {
                int count=path.CurveIndices.Count,first=curves.Count;var line=new LanePolyline(path.Points);
                var points=Enumerable.Range(0,count+1).Select(i=>line.At(line.Length*i/count)).ToArray();
                for(int i=0;i<count;i++)curves.Add(new(first+i,points[i],points[i+1],points[i+1]-points[i],points[i+1]-points[i],
                    i+1<count?[first+i+1]:[],i>0?[first+i-1]:[]));
                paths.Add(path with{CurveIndices=Enumerable.Range(first,count).ToArray(),Points=points,
                    CurveStartPointIndices=Enumerable.Range(0,count).ToArray()});
            }
            var geometry=new NativeLaneGeometry(family.Key,curves);
            foreach(var pair in family.Zip(paths))replacements[pair.First]=pair.Second with{NativeGeometry=geometry};
        }
        return new NativeLaneGraph(source.Select(e=>e with{LanePaths=e.LanePaths.Select(p=>replacements[p]).ToArray()}).ToArray());
    }
}
