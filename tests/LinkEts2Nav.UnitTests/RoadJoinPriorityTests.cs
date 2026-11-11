using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using System.Numerics;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadJoinPriorityTests
{
    private static RoadJoinEntryMeasurement Entry(ulong id,int lanes)=>new(id,lanes,0,0,0);

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
    public void MultipleExitsOrLaneContractionAreNotIndependentAdditions()
    {
        Assert.Empty(RoadJoinTopology.IndependentEntries([new(1,0,3,0),new(1,0,4,0),new(2,0,3,1)],3));
        Assert.Empty(RoadJoinTopology.IndependentEntries([new(1,0,3,0),new(1,1,3,0),new(2,0,3,1)],3));
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
    public void LoadedGraphAnnotationIsSharedWithOfficialRouteAndRestoresStraightConfirmation()
    {
        var scene=Scene();var paths=new RoadPathSource(scene.Map,[scene.Main,scene.Side]);
        var graph=paths.GetNavigationGraph();var main=graph.Single(e=>e.Id==scene.Main.Id);
        Assert.Equal(scene.Main.StartNodeUid,main.JoinPriority!.MainEntry);
        var route=RouteSnapshot.Parse([new(scene.Before.Node.Uid,2150,100),new(scene.Before.ForwardNode.Uid,2050,90),
            new(scene.After.Node.Uid,2000,80),new(scene.After.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        var selected=paths.GetPath(new(0,0,50),-Vector3.UnitZ,route,RouteAvailability.Available);
        Assert.Same(main.JoinPriority,selected.Single(e=>e.Id==main.Id).JoinPriority);
        var detector=new NavigationManeuverDetector();detector.FindSequence(selected,50,graph,out double end,out bool unknown,out double first);
        Assert.False(unknown);Assert.True(double.IsPositiveInfinity(first));Assert.True(end-50>800);
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
}
