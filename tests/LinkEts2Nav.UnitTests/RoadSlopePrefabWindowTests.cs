using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadSlopePrefabWindowTests
{
    private static DirectedRoad Prefab(ulong id,ulong from,ulong to,Vector3 start,Vector3 end,int lanes=1)
        =>new($"prefab:{id:X16}:0:1",from,to,[start,end])
        {
            LanePaths=Enumerable.Range(0,lanes).Select(lane=>new PrefabLanePath(id,from,to,lane,lane,lane,lane,lanes,lanes,
                [lane],[start+new Vector3(lane*3,0,0),end+new Vector3(lane*3,0,0)],Vector3.Distance(start,end))).ToArray()
        };
    private static Dictionary<ulong,List<Road>> Roads(params Road[] roads)
        =>roads.SelectMany(r=>new[]{(Node:r.Node.Uid,Road:r),(Node:r.ForwardNode.Uid,Road:r)})
            .GroupBy(p=>p.Node).ToDictionary(g=>g.Key,g=>g.Select(p=>p.Road).ToList());
    private static DirectedRoad OtherPort(DirectedRoad edge,string id)=>edge with
        {Id=id,LanePaths=edge.LanePaths.Select(p=>p with{CurveIndices=[8]}).ToArray()};

    [Theory]
    [InlineData(false,1)]
    [InlineData(true,1)]
    [InlineData(false,2)]
    [InlineData(true,3)]
    public void NativePrefabWindowsRespectTravelDirectionAndParallelLanes(bool incoming,int lanes)
    {
        var path=Prefab(1,10,20,new(0,0,0),new(0,3,30),lanes);
        var sampler=new RoadSlopeWindowSampler(Roads(), [path]);
        var sample=sampler.Sample(incoming?20UL:10UL,incoming);
        Assert.Null(sample.Failure);Assert.Equal(Math.Atan(.1)*180/Math.PI,sample.AngleDegrees!.Value,6);
        Assert.Equal(20,sample.CoveredMeters);Assert.Equal([1UL],sample.Prefabs);Assert.Empty(sample.Roads);
        Assert.Same(sample,sampler.Sample(incoming?20UL:10UL,incoming));
    }

    [Fact]
    public void WindowContinuesAcrossTwoPrefabsWithoutInventingAControlNodeConnector()
    {
        var first=Prefab(1,10,20,new(0,0,0),new(0,1,10));
        // Deliberately displaced lane endpoints: the shared UID connects them, but the gap adds no mileage or rise.
        var second=Prefab(2,20,30,new(0,100,100),new(0,101,110));
        var sample=new RoadSlopeWindowSampler(Roads(), [first,second]).Sample(30,true);
        Assert.Null(sample.Failure);Assert.Equal(20,sample.CoveredMeters);
        Assert.Equal(Math.Atan(.1)*180/Math.PI,sample.AngleDegrees!.Value,6);
        Assert.Equal(2,sample.Prefabs.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeWindowContinuesBetweenRoadAndPrefab(bool incoming)
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,0),new(0,0,10),default);
        var prefab=Prefab(1,road.ForwardNode.Uid,30,new(0,0,10),new(0,0,40));
        var sampler=new RoadSlopeWindowSampler(Roads(road),[prefab]);
        var sample=sampler.Sample(incoming?30UL:road.Node.Uid,incoming);
        Assert.Null(sample.Failure);Assert.Equal(0,sample.AngleDegrees);Assert.Equal(20,sample.CoveredMeters);
        if(!incoming)Assert.Single(sample.Roads);
    }

    [Fact]
    public void PrefabShorterThanWindowCanContinueOntoRoad()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,new(0,0,10),new(0,0,50),default);
        var prefab=Prefab(1,10,road.Node.Uid,new(0,0,0),new(0,0,10));
        var sample=new RoadSlopeWindowSampler(Roads(road),[prefab]).Sample(10,false);
        Assert.Null(sample.Failure);Assert.Equal(0,sample.AngleDegrees);Assert.Single(sample.Roads);Assert.Single(sample.Prefabs);
    }

    [Fact]
    public void CurrentScoredPrefabIsExcludedFromBothInterfaceWindows()
    {
        var before=Prefab(1,10,20,new(0,0,0),new(0,0,30));
        var current=Prefab(2,20,10,new(0,0,30),new(0,5,0));
        var sampler=new RoadSlopeWindowSampler(Roads(), [before,current]);
        var sample=sampler.Sample(20,true,"0000000000000002");
        Assert.Null(sample.Failure);Assert.Equal(0,sample.AngleDegrees);Assert.Equal([1UL],sample.Prefabs);
        Assert.NotSame(sample,sampler.Sample(20,true));
    }

    [Fact]
    public void ASecondRampStopsReverseSamplingBeforeItsAlternatingSlopes()
    {
        var left=Prefab(1,10,30,new(0,0,0),new(0,0,30));
        var right=OtherPort(Prefab(1,20,30,new(10,4,0),new(0,0,30)),"prefab:0000000000000001:2:1");
        var sample=new RoadSlopeWindowSampler(Roads(), [left,right]).Sample(30,true);
        Assert.Null(sample.AngleDegrees);
        Assert.Equal(0,sample.CoveredMeters);
        Assert.Null(sample.MapIssue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctPortsWithMatchingWindowsProvideOneSlopeWithoutChoosingARoute(bool incoming)
    {
        var left=incoming?Prefab(1,10,30,new(0,0,0),new(0,3,30)):Prefab(1,30,10,new(0,0,0),new(0,3,30));
        var right=OtherPort(incoming?Prefab(1,20,30,new(10,0,0),new(10,3,30)):Prefab(1,30,20,new(10,0,0),new(10,3,30)),
            "prefab:0000000000000001:2:1");
        var crossing=new PrefabJunctionInfo(2,2,1);
        var sample=new RoadSlopeWindowSampler(Roads(),[left with{Junction=crossing},right with{Junction=crossing}]).Sample(30,incoming);
        Assert.Null(sample.MapIssue);Assert.Null(sample.Failure);Assert.Equal(20,sample.CoveredMeters);
        Assert.Equal(Math.Atan(.1)*180/Math.PI,sample.AngleDegrees!.Value,6);
    }

    [Fact]
    public void OneIncompletePortCannotBeDiscardedInFavorOfTheCompletePort()
    {
        var complete=Prefab(1,10,30,new(0,0,0),new(0,0,30));
        var incomplete=OtherPort(Prefab(1,20,30,new(3,0,25),new(3,0,30)),"prefab:0000000000000001:2:1");
        var missing=new MissingMapObjects([new(MapObjectKind.Item,99)],"missing continuation");
        var crossing=new PrefabJunctionInfo(2,2,1);
        var sample=new RoadSlopeWindowSampler(Roads(),[complete with{Junction=crossing},incomplete with{Junction=crossing}],_=>missing).Sample(30,true);
        Assert.Null(sample.AngleDegrees);Assert.Equal(5,sample.CoveredMeters);
        var issue=Assert.IsType<MissingMapObjects>(sample.MapIssue);
        Assert.Equal(missing.Keys,issue.Keys);Assert.Equal(missing.Reason,issue.Reason);Assert.NotNull(sample.Failure);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void SamplingUsesTheAvailableRoadBeforeASecondRampOrPublicSplit(bool common,bool crossing)
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,Vector3.Zero,new(0,1,12),default);
        var left=Prefab(1,road.ForwardNode.Uid,30,new(0,1,12),new(0,0,50));
        var right=OtherPort(Prefab(1,road.ForwardNode.Uid,40,new(3,1,12),new(3,0,50)),"prefab:0000000000000001:0:2");
        var junction=crossing?new PrefabJunctionInfo(2,2,1):null;
        var sample=new RoadSlopeWindowSampler(Roads(road),[left with{Junction=junction},right with{Junction=junction}])
            .Sample(road.Node.Uid,false,common:common);
        Assert.Null(sample.Failure);
        Assert.Equal(Math.Atan(1d/12)*180/Math.PI,sample.AngleDegrees!.Value,6);
        Assert.Equal(12,sample.CoveredMeters,6);
        Assert.Single(sample.Roads);Assert.Empty(sample.Prefabs);
    }

    [Fact]
    public void InconsistentParallelLaneSlopesAreRecordedSeparatelyFromMapDependencies()
    {
        var path=Prefab(1,10,20,new(0,0,0),new(0,0,30),2);
        path=path with{LanePaths=[path.LanePaths[0],path.LanePaths[1] with{Points=[new(3,0,0),new(3,3,30)]}]};
        var sample=new RoadSlopeWindowSampler(Roads(), [path]).Sample(20,true);
        Assert.Null(sample.AngleDegrees);Assert.True(sample.InconsistentLaneSlopes);Assert.Null(sample.MapIssue);
    }

    [Fact]
    public void LoadedEndOfTheRoadUsesTheSlopeOverTheAvailableDistance()
    {
        var sample=new RoadSlopeWindowSampler(Roads(), [Prefab(1,10,20,new(0,0,0),new(0,1,5))],
            _=>new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,"Loaded depot with no continuation.")).Sample(20,true);
        Assert.Equal(Math.Atan(.2)*180/Math.PI,sample.AngleDegrees!.Value,6);
        Assert.Equal(5,sample.CoveredMeters);Assert.Null(sample.Failure);Assert.Null(sample.MapIssue);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.0000001f)]
    public void EffectivelyZeroRoadLengthDoesNotContributeSlope(float recordedLength)
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,Vector3.Zero,new(0,1,5),default);
        road.Length=recordedLength;
        var sample=new RoadSlopeWindowSampler(Roads(road)).Sample(road.Node.Uid,false);
        Assert.Equal(0,sample.CoveredMeters);Assert.Null(sample.AngleDegrees);Assert.Null(sample.MapIssue);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidSourceRoadLengthReportsInvalidInput(float recordedLength)
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,Vector3.Zero,new(0,1,5),default);
        road.Length=recordedLength;
        var sampler=new RoadSlopeWindowSampler(Roads(road));
        Assert.Throws<InvalidDataException>(()=>sampler.Sample(road.Node.Uid,false));
    }

    [Fact]
    public void NonfinitePrefabSamplesAreExplicitlyUnsupported()
    {
        var path=Prefab(1,10,20,Vector3.Zero,new(0,1,5));
        var malformed=path with{LanePaths=[path.LanePaths[0] with{Points=[Vector3.Zero,new(float.NaN,1,5)]}]};
        var sampler=new RoadSlopeWindowSampler(Roads(),[malformed]);
        Assert.Throws<NotSupportedException>(()=>sampler.Sample(10,false));
    }

    [Fact]
    public void NativeCurveStartGapDoesNotContributeToTheSlopeWindow()
    {
        var path=Prefab(1,10,20,new(0,0,0),new(0,0,30));
        path=path with{LanePaths=[path.LanePaths[0] with{Points=[new(0,0,0),new(0,1,10),new(0,100,100),new(0,101,110)],CurveStartPointIndices=[0,2]}]};
        var sample=new RoadSlopeWindowSampler(Roads(), [path]).Sample(20,true);
        Assert.Null(sample.Failure);Assert.Equal(20,sample.CoveredMeters);
        Assert.Equal(Math.Atan(.1)*180/Math.PI,sample.AngleDegrees!.Value,6);
    }
}

