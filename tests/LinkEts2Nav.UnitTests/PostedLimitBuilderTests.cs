using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class PostedLimitBuilderTests
{
    private static Vector3 P(float x,float z=0)=>new(x,0,z);
    private static DirectedRoad Road(ulong id,ulong start,ulong end,params Vector3[] points)=>new(id.ToString("X16"),start,end,points);
    private static SpeedLimitData Data(DirectedRoad[] roads,params LimitSign[] signs)=>new(
        roads.ToDictionary(r=>Convert.ToUInt64(r.Id,16),r=>new LimitRoad(r,"local_road")),signs,[],
        new Dictionary<string,TruckSpeedLimit>{{"norway/local_road",new(80,50,80)},{"norway/motorway",new(80,80,80)}},"norway");
    private static SpeedLimitData Build(SpeedLimitData data,IReadOnlyList<DirectedRoad> links)
        =>PostedLimitBuilder.Build(data,links,TestContext.Current.CancellationToken);
    private static SpeedLimitResult Query(SpeedLimitData data,Vector3 p,Vector3? direction=null)
        =>new SpeedLimitResolver(data,new RoadPathSource(new TruckLib.ScsMap.Map())).ResolvePosition(p,direction??Vector3.UnitX);

    [Fact]
    public void PostedIntervalCrossesSectorsAndIsNotBoundedByOneKilometre()
    {
        var a=Road(1,10,11,P(0),P(3000));var b=Road(2,11,12,P(3000),P(4200));var c=Road(3,12,13,P(4200),P(5000));
        var data=Build(Data([a,b,c],new LimitSign("60",P(100,5),Vector3.UnitX,false,new(SetKph:60))),[]);
        Assert.Equal(60,Query(data,P(4500)).Kph);
        Assert.Equal(80,Query(data,P(4500),-Vector3.UnitX).Kph);
        Assert.Equal(60,Assert.Single(data.Roads[3].ForwardSignLimits).State.Kph);
    }
    [Fact]
    public void NativeOneWayPrefabPropagatesToEveryExitWithoutResetting()
    {
        var a=Road(1,10,11,P(0),P(100));var b=Road(2,12,13,P(120),P(250));var c=Road(3,14,15,P(120,40),P(250,40));
        DirectedRoad[] links=[new("prefab:ABC:0:1",11,12,[P(100),P(120)]),new("prefab:ABC:0:2",11,14,[P(100),P(120,40)])];
        var data=Build(Data([a,b,c],new LimitSign("60",P(50,5),Vector3.UnitX,false,new(SetKph:60))),links);
        Assert.Equal(60,Query(data,P(200)).Kph);Assert.Equal(60,Query(data,P(200,40)).Kph);
        Assert.Equal(60,Query(data,P(110)).Kph);
    }
    [Fact]
    public void ExplicitResetEndsAnIntervalAndSameRoadKeepsOrderedSegments()
    {
        var road=Road(1,10,11,P(0),P(1000));
        var data=Build(Data([road],new LimitSign("60",P(100,5),Vector3.UnitX,false,new(SetKph:60)),
            new LimitSign("end",P(700,5),Vector3.UnitX,false,new(Reset:true))),[]);
        Assert.Equal(80,Query(data,P(50)).Kph);Assert.Equal(60,Query(data,P(500)).Kph);Assert.Equal(80,Query(data,P(800)).Kph);
        Assert.Equal(3,data.Roads[1].ForwardSignLimits.Count);
    }
    [Fact]
    public void ConflictingMergeWarnsAndAFollowingSignRestoresKnownState()
    {
        var a=Road(1,10,12,P(0),P(100));var b=Road(2,11,12,P(0,30),P(100));var c=Road(3,12,13,P(100),P(400));
        var data=Build(Data([a,b,c],new LimitSign("50",P(30,1),Vector3.UnitX,false,new(SetKph:50)),
            new LimitSign("60",P(30,22),Vector3.Normalize(P(100,-30)),false,new(SetKph:60)),
            new LimitSign("reset",P(300,5),Vector3.UnitX,false,new(SetKph:70))),[]);
        Assert.Contains(data.Diagnostics,l=>l.StartsWith("限速汇合冲突："));
        Assert.Null(Query(data,P(200)).Kph);Assert.Equal(70,Query(data,P(350)).Kph);
    }
    [Fact]
    public void ReloadingAWindowRebuildsOnlyPublishedDataAndLeavesOldSnapshotStable()
    {
        var a=Road(1,10,11,P(0),P(100));var b=Road(2,11,12,P(100),P(300));
        var old=Build(Data([b]),[]);
        var updated=Build(Data([a,b],new LimitSign("60",P(30,5),Vector3.UnitX,false,new(SetKph:60))),[]);
        Assert.Equal(80,Query(old,P(200)).Kph);Assert.Equal(60,Query(updated,P(200)).Kph);
        Assert.Equal(80,Query(old,P(200)).Kph);
    }
    [Fact]
    public void AreaLookupUsesOwnerSectorAmongNearestFourAndChecksHeight()
    {
        var polygon=new[]{P(3900,-20),P(4300,-20),P(4300,20),P(3900,20)};
        var area=new LimitRegion("area",polygon,3,false,30){OwnerSector=new(0,0)};
        var far=area with{Id="outside-contract",OwnerSector=new(8,8)};
        var index=new LimitRegionIndex([area,far]);
        Assert.Equal("area",Assert.Single(index.At(P(4100))).Id);
        Assert.Empty(index.At(new(4100,5,0)));
        Assert.Equal(4,LimitRegionIndex.NearestFour(P(-1,-1)).Distinct().Count());
    }
    [Fact]
    public void UrbanDefaultCanBeRaisedBySignButExplicitAreasAndMaximumStillCapIt()
    {
        var a=Road(1,10,11,P(0),P(1000));var source=Data([a],new LimitSign("70",P(100,5),Vector3.UnitX,false,new(SetKph:70)));
        source=source with{Regions=[new("urban",[P(0,-50),P(1000,-50),P(1000,50),P(0,50)],0,true)]};
        Assert.Equal(70,Query(Build(source,[]),P(500)).Kph);
        source=source with{Regions=[..source.Regions,new("numeric",[P(0,-50),P(1000,-50),P(1000,50),P(0,50)],0,false,40)]};
        Assert.Equal(40,Query(Build(source,[]),P(500)).Kph);
    }
    [Fact]
    public void EqualMergeDoesNotWarnAndCycleTerminates()
    {
        var a=Road(1,10,12,P(0),P(100));var b=Road(2,11,12,P(0,30),P(100));var c=Road(3,12,13,P(100),P(400));
        var data=Build(Data([a,b,c],new LimitSign("a",P(30,1),Vector3.UnitX,false,new(SetKph:60)),
            new LimitSign("b",P(30,22),Vector3.Normalize(P(100,-30)),false,new(SetKph:60))),[]);
        Assert.Equal(60,Query(data,P(200)).Kph);
        Assert.DoesNotContain(data.Diagnostics,l=>l.StartsWith("限速汇合冲突："));
        var loop=Road(4,13,10,P(400),P(400,100),P(0,100),P(0));
        data=Build(Data([a,c,loop],new LimitSign("60",P(30,1),Vector3.UnitX,false,new(SetKph:60))),[]);
        Assert.Equal(60,Query(data,P(200)).Kph);
        Assert.DoesNotContain(data.Diagnostics,l=>l.StartsWith("限速汇合冲突："));
    }

    [Fact]
    public void ASignBeyondAnEndpointDoesNotAttachToThePrecedingRoad()
    {
        var before=Road(1,10,11,P(0),P(100));var after=Road(2,11,12,P(100),P(300));
        var data=Build(Data([before,after],new LimitSign("60",P(110,5),Vector3.UnitX,false,new(SetKph:60))),[]);
        Assert.Equal(80,Query(data,P(90)).Kph);Assert.Equal(60,Query(data,P(200)).Kph);
    }

    [Fact]
    public void MotorwayUsesCountryUrbanMotorwayValueRatherThanIgnoringEveryArea()
    {
        var road=Road(1,10,11,P(0),P(1000));var source=Data([road]);
        source=source with{Roads=new Dictionary<ulong,LimitRoad>{{1,new(road,"motorway")}},
            Regions=[new("urban",[P(0,-50),P(1000,-50),P(1000,50),P(0,50)],0,true)]};
        Assert.Equal(80,Query(Build(source,[]),P(500)).Kph);
        source=source with{Regions=[..source.Regions,new("work-zone",[P(0,-50),P(1000,-50),P(1000,50),P(0,50)],0,false,40)]};
        Assert.Equal(40,Query(Build(source,[]),P(500)).Kph);
    }

    [Fact]
    public void ExitingPrefabCannotImmediatelyReenterAnotherPathInTheSamePrefab()
    {
        var a=Road(1,10,11,P(0),P(100));var b=Road(2,12,13,P(120),P(250));var c=Road(3,14,15,P(120,40),P(250,40));
        DirectedRoad[] links=[new("prefab:ABC:0:1",11,12,[P(100),P(120)]),
            new("prefab:ABC:1:2",12,14,[P(120),P(120,40)])];
        var data=Build(Data([a,b,c],new LimitSign("60",P(50,5),Vector3.UnitX,false,new(SetKph:60))),links);
        Assert.Equal(60,Query(data,P(200)).Kph);
        Assert.Equal(80,Query(data,P(200,40)).Kph);
    }

}
