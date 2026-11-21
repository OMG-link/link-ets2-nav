using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using System.Numerics;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadJoinPriorityTests
{
    private static RoadJoinEntryMeasurement Entry(ulong id,int lanes)=>new(id,lanes,0,0,0);

#if DEBUG
    [Fact]
    public void RepeatedDirectedPrefabPassageViolatesAnnotationContract()
    {
        var passage = new DirectedRoad("prefab:0000000000000001:0:1", 10, 20, [Vector3.Zero, Vector3.UnitX]);
        var builder = new RoadJoinPriorityBuilder(new Dictionary<ulong, List<Road>>());

        Assert.Throws<NavigationContractException>(() => builder.Annotate([passage, passage]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScoringPassageRequiresBothNativeTangents(bool missingEntry)
    {
        var scene = Scene();
        var passage = missingEntry ? scene.Side with { NativeEntryTangent = null }
            : scene.Side with { NativeExitTangent = null };
        var builder = new RoadJoinPriorityBuilder(Index(scene.Map.MapItems.Values.OfType<Road>().ToArray()));

        Assert.Throws<NavigationContractException>(() => builder.Annotate([scene.Main, passage]));
    }
#endif

    [Fact]
    public void ExactlyFivePointAdvantageQualifies()
    {
        var result=RoadJoinPriorityScorer.Evaluate([Entry(1,3),Entry(2,1)]);
        Assert.Equal(1UL,result.MainEntry);Assert.Equal(5,result.WinningMargin);
        Assert.Equal(30,result.Scores.Sum(s=>s.Total),10);
    }
    [Theory]
    [InlineData(2,1)]
    [InlineData(1,1)]
    public void SmallerMarginOrTieDoesNotGuess(int a,int b)
    {
        var result=RoadJoinPriorityScorer.Evaluate([Entry(1,a),Entry(2,b)]);
        Assert.Null(result.MainEntry);Assert.NotEmpty(result.Scores);
    }
    [Fact]
    public void WinnerMustBeatEveryEntranceNotJustTheLowestScorer()
    {
        var result=RoadJoinPriorityScorer.Evaluate([Entry(1,10),Entry(2,5),Entry(3,1)]);
        Assert.True(result.Scores[0].Total-result.Scores[2].Total>5);
        Assert.Null(result.MainEntry);
        Assert.Equal(1UL,RoadJoinPriorityScorer.Evaluate([Entry(1,6),Entry(2,1),Entry(3,1)]).MainEntry);
    }
    [Fact]
    public void MissingCompetitorIsRetainedAndPreventsAConclusion()
    {
        var result=RoadJoinPriorityScorer.Evaluate([Entry(1,3),Entry(2,1) with{IncomingSlopeDegrees=null,Failure="missing window"}]);
        Assert.Null(result.MainEntry);Assert.Equal(2,result.Measurements.Count);Assert.Empty(result.Scores);
        Assert.Contains("missing window",result.Reason);
    }
    [Fact]
    public void IndependentEntriesUseScoringButSharedDestinationDoesNot()
    {
        LaneConnection[] addition=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,2)];
        var priority=RoadJoinPriorityScorer.Evaluate([new(1,2,0,0,0),new(2,1,5,0,15)]);
        Assert.Equal(RoadJoinRole.Continuing,RoadJoinClassifier.Classify(addition,1,3,priority).Role);
        Assert.Equal(1UL,RoadJoinClassifier.Classify(addition,2,3,priority).MainEntry);
        Assert.Equal(RoadJoinRole.Entering,RoadJoinClassifier.Classify(addition,2,3,priority).Role);
        LaneConnection[] shared=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,1)];
        Assert.Empty(RoadJoinTopology.IndependentEntries(shared,3));
        var misleading=priority with{MainEntry=2};
        Assert.Equal(RoadJoinRole.Continuing,RoadJoinClassifier.Classify(shared,1,3,misleading).Role);
    }
    [Fact]
    public void MultipleExitsAreNotIndependentButAContractionIntoItsOwnAddedLaneIs()
    {
        Assert.Empty(RoadJoinTopology.IndependentEntries([new(1,0,3,0),new(1,0,4,0),new(2,0,3,1)],3));
        Assert.Equal(2,RoadJoinTopology.IndependentEntries([new(1,0,3,0),new(1,1,3,0),new(2,0,3,1)],3).Count);
    }
    [Fact]
    public void RealTwentyMetreMeasurementsReproduceTheTwoObservedMainEntrances()
    {
        var first=RoadJoinPriorityScorer.Evaluate([new(1,2,-1.031294,-.878744,0),new(2,1,1.793694,-.878744,15)]);
        var second=RoadJoinPriorityScorer.Evaluate([new(1,2,0,.160814,0),new(2,1,-2.653662,.160814,10)]);
        Assert.Equal(1UL,first.MainEntry);Assert.Equal(21.6082,first.WinningMargin!.Value,3);
        Assert.Equal(1UL,second.MainEntry);Assert.Equal(21.3000,second.WinningMargin!.Value,3);
    }

    private static Dictionary<ulong,List<Road>> Index(params Road[] roads)
        =>roads.SelectMany(r=>new[]{(Node:r.Node.Uid,Road:r),(Node:r.ForwardNode.Uid,Road:r)})
            .GroupBy(p=>p.Node).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Road).ToList());

    [Fact]
    public void SlopeWindowExtendsAcrossNativeRoadItemsAndCachesOnlyItsSnapshot()
    {
        var map=new TruckLib.ScsMap.Map();var a=Road.Add(map,new(0,0,0),new(0,0,10),default);
        var b=a.Append(new(0,0,50));var sampler=new RoadSlopeWindowSampler(Index(a,b));
        var value=sampler.Sample(a.Node.Uid,false);
        Assert.Null(value.Failure);Assert.Equal(20,value.CoveredMeters);Assert.Equal(0,value.AngleDegrees);
        Assert.Equal(2,value.Roads.Count);Assert.Same(value,sampler.Sample(a.Node.Uid,false));
    }
    [Fact]
    public void IncompleteOrAmbiguousSlopeWindowsDoNotPretendToBeFlat()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(0,0,10),default);
        var partial=new RoadSlopeWindowSampler(Index(road)).Sample(road.Node.Uid,false);
        Assert.Null(partial.AngleDegrees);Assert.InRange(partial.CoveredMeters,9.9,10.1);
        var ambiguous=Index(road);ambiguous[road.Node.Uid].Add(road);
        Assert.Null(new RoadSlopeWindowSampler(ambiguous).Sample(road.Node.Uid,false).AngleDegrees);
    }

    private static (TruckLib.ScsMap.Map Map,DirectedRoad Main,DirectedRoad Side,Road Before,Road After) Scene(bool complete=true)
    {
        var map=new TruckLib.ScsMap.Map();var before=Road.Add(map,new(0,0,100),new(0,0,0),default);
        var sideRoad=Road.Add(map,new(70,10,100),new(10,0,0),default);
        var after=Road.Add(map,new(0,0,-50),new(0,0,-2050),default);
        LaneConnection[] lanes=[new(before.ForwardNode.Uid,0,after.Node.Uid,0),new(before.ForwardNode.Uid,1,after.Node.Uid,1),
            new(sideRoad.ForwardNode.Uid,0,after.Node.Uid,2)];
        var main=new DirectedRoad("prefab:42:0:1",before.ForwardNode.Uid,after.Node.Uid,
            [new(0,0,0),new(0,0,0),new(0,0,-50),new(0,0,-50)])
            {LaneConnections=lanes,NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=-Vector3.UnitZ};
        var side=main with{Id="prefab:42:2:1",StartNodeUid=sideRoad.ForwardNode.Uid,
            Points=[new(10,0,0),new(10,0,0),new(0,0,-50),new(0,0,-50)],NativeEntryTangent=Vector3.Normalize(new(-.25f,0,-1))};
        if(!complete)map.MapItems.Remove(sideRoad.Uid);
        return(map,main,side,before,after);
    }
    [Fact]
    public void LoadedPriorityAnnotationDoesNotReplaceMissingNativeLaneContinuity()
    {
        var scene=Scene();var paths=new RoadPathSource(scene.Map,[scene.Main,scene.Side]);
        var graph=paths.GetNavigationGraph();var main=graph.Single(e=>e.Id==scene.Main.Id);
        Assert.Equal(scene.Main.StartNodeUid,main.JoinPriority!.MainEntry);
        var route=RouteSnapshot.Parse([new(scene.Before.Node.Uid,2150,100),new(scene.Before.ForwardNode.Uid,2050,90),
            new(scene.After.Node.Uid,2000,80),new(scene.After.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        var selected=paths.GetPath(new(0,0,50),-Vector3.UnitZ,route,RouteAvailability.Available);
        Assert.Same(main.JoinPriority,selected.Single(e=>e.Id==main.Id).JoinPriority);
        var detector=new NavigationManeuverDetector();detector.FindSequence(selected,50,graph,out double end,out bool unknown,out double first);
        Assert.True(unknown);Assert.Equal(100,first,1);
        Assert.Contains(detector.Components.Issues,i=>i is MapQueryFailure {Kind:MapQueryFailureKind.DefinitionUnavailable});
    }
    [Fact]
    public void AdjacentPrefabProvidesTheMissingEntranceWindowBeforeMainRoadScoring()
    {
        var scene=Scene();scene.Map.MapItems.Remove(scene.Before.Uid);
        var previous=new DirectedRoad("prefab:41:0:1",scene.Before.Node.Uid,scene.Before.ForwardNode.Uid,
            [new(0,0,100),new(0,0,0)])
        {
            LanePaths=Enumerable.Range(0,2).Select(lane=>new PrefabLanePath(41,scene.Before.Node.Uid,
                scene.Before.ForwardNode.Uid,lane,lane,lane,lane,2,2,[lane],
                [new(lane*3,0,100),new(lane*3,0,0)],100)).ToArray()
        };
        var paths=new RoadPathSource(scene.Map,[previous,scene.Main,scene.Side]);
        var main=paths.GetNavigationGraph().Single(e=>e.Id==scene.Main.Id);
        Assert.Equal(scene.Main.StartNodeUid,main.JoinPriority!.MainEntry);
        Assert.All(main.JoinPriority.Measurements,m=>Assert.Null(m.Failure));
        Assert.Equal(2,main.JoinPriority.Scores.Count);Assert.True(main.JoinPriority.WinningMargin>=5);
    }
    [Fact]
    public void MissingSideRoadStillCompetesAndKeepsTheUnknownJoinBarrier()
    {
        var scene=Scene(false);var paths=new RoadPathSource(scene.Map,[scene.Main,scene.Side]);
        var graph=paths.GetNavigationGraph();var main=graph.Single(e=>e.Id==scene.Main.Id);
        Assert.Null(main.JoinPriority!.MainEntry);Assert.Equal(2,main.JoinPriority.Measurements.Count);
        Assert.Equal(RoadJoinRole.Ambiguous,RoadJoinClassifier.Classify(main).Role);
        var approach=graph.Single(e=>e.StartNodeUid==scene.Before.Node.Uid&&e.EndNodeUid==scene.Before.ForwardNode.Uid);
        var outgoing=graph.Single(e=>e.StartNodeUid==scene.After.Node.Uid&&e.EndNodeUid==scene.After.ForwardNode.Uid);
        new NavigationManeuverDetector().FindSequence([approach,main,outgoing],0,graph,out _,out bool unknown,out double first);
        Assert.True(unknown);Assert.Equal(100,first,1);
    }
    [Fact]
    public void SharedExitSlotsStillParticipateInTheComponentPriorityCompetition()
    {
        var scene=Scene();var lanes=scene.Main.LaneConnections!.Select(c=>c with{ExitLane=0}).ToArray();
        var edges=new[]{scene.Main with{LaneConnections=lanes},scene.Side with{LaneConnections=lanes}};
        var result=new RoadJoinPriorityBuilder(Index(scene.Map.MapItems.Values.OfType<Road>().ToArray())).Annotate(edges);
        Assert.All(result,e=>Assert.NotNull(e.JoinPriority));
        Assert.All(result,e=>Assert.Equal(2,e.JoinPriority!.Measurements.Count));
    }

    [Fact]
    public void DirectionScoringAcceptsSmallHorizontalTangentComponents()
    {
        var scene = Scene();
        var passage = scene.Side with { NativeEntryTangent = new Vector3(.0001f, 1, 0) };
        var builder = new RoadJoinPriorityBuilder(Index(scene.Map.MapItems.Values.OfType<Road>().ToArray()));

        var annotated = builder.Annotate([scene.Main, passage]);

        var measurement = Assert.Single(annotated[0].JoinPriority!.Measurements, m => m.Entry == passage.StartNodeUid);
        Assert.Equal(90d, measurement.DirectionDifferenceDegrees!.Value, 8);
        Assert.NotEmpty(annotated[0].JoinPriority!.Scores);
    }
    [Fact]
    public void OutletCompetitionUsesTheSameThirtyPointScorerWithReversedTravelMeasurements()
    {
        var scene=Scene();var road=Road.Add(scene.Map,new(30,10,-30),new(2030,400,-430),default);
        LaneConnection[] lanes=[new(scene.Main.StartNodeUid,0,scene.Main.EndNodeUid,0),
            new(scene.Main.StartNodeUid,1,scene.Main.EndNodeUid,1),new(scene.Main.StartNodeUid,1,road.Node.Uid,0)];
        var main=scene.Main with{LaneConnections=lanes};
        var side=main with{Id="prefab:42:0:2",EndNodeUid=road.Node.Uid,
            Points=[Vector3.Zero,new(30,10,-30)],NativeExitTangent=Vector3.Normalize(new(1,0,-.25f))};
        var result=new RoadJoinPriorityBuilder(Index(scene.Map.MapItems.Values.OfType<Road>().ToArray())).Annotate([main,side]);
        Assert.All(result,e=>Assert.Equal(main.EndNodeUid,e.SplitPriority!.MainEntry));
        Assert.Equal(30,result[0].SplitPriority!.Scores.Sum(s=>s.Total),9);
        Assert.Equal(2,result[0].SplitPriority!.Measurements.Count);
    }
}
