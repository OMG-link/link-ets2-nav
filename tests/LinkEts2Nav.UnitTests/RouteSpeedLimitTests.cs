using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class RouteSpeedLimitTests
{
    private static Vector3 P(float x,float z=0)=>new(x,0,z);
    private static DirectedRoad R(ulong id,ulong start,ulong end,params Vector3[] ps)=>new(id.ToString("X16"),start,end,ps);
    private static readonly DirectedRoad Road=R(1,10,11,P(0),P(1000));
    private static LimitSign Sign(string id,float x,LimitAction action)=>new(id,P(x,5),Vector3.UnitX,false,action);
    private static SpeedLimitData Source(DirectedRoad[] roads,params LimitSign[] signs)=>new(
        roads.ToDictionary(r=>Convert.ToUInt64(r.Id,16),r=>new LimitRoad(r,"local_road")),signs,[],
        new Dictionary<string,TruckSpeedLimit>{{"test/local_road",new(80,50,120)}},"test");
    private static SpeedLimitData Build(SpeedLimitData data,params DirectedRoad[] prefabs)
        =>ProjectedSignBuilder.Build(data,prefabs,TestContext.Current.CancellationToken);
    private static SpeedLimitResult Sim(SpeedLimitData data,DirectedRoad[] path,Vector3? start=null,
        Vector3? target=null,double? seed=80,Vector3? heading=null)
        =>new SpeedLimitResolver(data,new RoadPathSource(new TruckLib.ScsMap.Map())).ResolveOnPath(start??P(0),
            heading??Vector3.UnitX,seed,new("camera",target??P(900),Vector3.UnitX),path);

    [Fact]
    public void MergedRoadUsesChosenApproachAndDoesNotBorrowTheOtherApproachsHigherSign()
    {
        var a=R(1,10,12,P(0),P(100));var b=R(2,11,12,P(0,30),P(100));var c=R(3,12,13,P(100),P(400));
        var data=Build(Source([a,b,c],new LimitSign("side80",P(30,22),Vector3.Normalize(P(100,-30)),false,new(SetKph:80))));
        Assert.Equal(50,Sim(data,[a,c],target:P(350),seed:50).Kph);
        Assert.Equal(80,Sim(data,[b,c],start:P(0,30),target:P(350),seed:50).Kph);
        Assert.DoesNotContain(data.Diagnostics,d=>d.StartsWith("限速汇合"));
    }
    [Fact]
    public void OnlyLastSignAppliesAndOneCancellationClearsEveryEarlierLimit()
    {
        var data=Build(Source([Road],Sign("100",100,new(SetKph:100)),Sign("70",200,new(SetKph:70)),
            Sign("40",300,new(SetKph:40)),Sign("cancel",400,new(Reset:true))));
        Assert.Equal(40,Sim(data,[Road],target:P(350)).Kph);
        Assert.Equal(80,Sim(data,[Road]).Kph);
    }
    [Fact]
    public void IgnoresSignsBehindPlayerAndBeyondCamera()
    {
        var data=Build(Source([Road],Sign("past",10,new(SetKph:30)),Sign("beyond",950,new(SetKph:110))));
        Assert.Equal(50,Sim(data,[Road],start:P(50),seed:50).Kph);
    }
    [Fact]
    public void SdkSeedBelongsToThisJourneyAndIsNotCachedByCameraId()
    {
        var data=Build(Source([Road]));
        Assert.Equal(50,Sim(data,[Road],seed:50).Kph);Assert.Equal(80,Sim(data,[Road],seed:80).Kph);
    }
    [Fact]
    public void MissingSeedStaysUnknownUntilASetOrCancellationSign()
    {
        Assert.Null(Sim(Build(Source([Road])),[Road],seed:null).Kph);
        Assert.Equal(60,Sim(Build(Source([Road],Sign("60",100,new(SetKph:60)))),[Road],seed:null).Kph);
        Assert.Equal(80,Sim(Build(Source([Road],Sign("cancel",100,new(Reset:true)))),[Road],seed:null).Kph);
    }
    [Fact]
    public void UncertainFutureSignCanBeRecoveredByASubsequentKnownSign()
    {
        var data=Build(Source([Road],Sign("unknown",100,new(Unsupported:true)),Sign("60",500,new(SetKph:60))));
        Assert.Null(Sim(data,[Road],target:P(300)).Kph);Assert.Equal(60,Sim(data,[Road]).Kph);
        Assert.Equal(50,Sim(data,[Road],start:P(200),target:P(300),seed:50).Kph);
    }
    [Fact]
    public void CrossSectorPathHasNoOneKilometreSimulationLimit()
    {
        var a=R(1,10,11,P(0),P(3000));var b=R(2,11,12,P(3000),P(5000));
        var data=Build(Source([a,b],Sign("60",100,new(SetKph:60))));
        Assert.Equal(60,Sim(data,[a,b],target:P(4500)).Kph);
    }
    [Fact]
    public void AGapOrCameraBehindPlayerCannotBorrowAnUnrelatedLimit()
    {
        var gap=R(2,20,21,P(1100),P(1300));var data=Build(Source([Road,gap]));
        Assert.Null(Sim(data,[Road,gap],target:P(1200)).Kph);
        Assert.Null(Sim(data,[Road],start:P(500),target:P(100)).Kph);
    }
    [Fact]
    public void NativePrefabSignsAreReadInTheChosenRouteOrder()
    {
        var a=R(1,10,11,P(0),P(100));var b=R(2,12,13,P(200),P(400));
        DirectedRoad bridge=new("prefab:ABC:0:1",11,12,[P(100),P(200)]);
        var data=Build(Source([a,b],Sign("60",150,new(SetKph:60))),bridge);
        Assert.Equal(60,Sim(data,[a,bridge,b],target:P(350)).Kph);
    }
    [Fact]
    public void ASignAtAContinuousPrefabInterfaceIsNotCompetingOwnership()
    {
        var a=R(1,10,11,P(-100),P(0));var b=R(2,14,15,P(120),P(300));
        DirectedRoad p=new("prefab:ABC:0:1",11,12,[P(0),P(100)]),q=new("prefab:DEF:0:1",12,14,[P(100),P(120)]);
        var data=Build(Source([a,b],Sign("60",100,new(SetKph:60))),p,q);
        Assert.Equal(60,Sim(data,[a,p,q,b],start:P(-50),target:P(250)).Kph);
    }
    [Fact]
    public void OppositeSignsAndSignsOnDisconnectedParallelRoadsDoNotBecomeRouteEvents()
    {
        var parallel=R(2,20,21,P(0,40),P(1000,40));
        var data=Build(Source([Road,parallel],new("opposite",P(100,5),-Vector3.UnitX,false,new(SetKph:30)),
            new("other",P(100,45),Vector3.UnitX,false,new(SetKph:100))));
        Assert.Equal(50,Sim(data,[Road],seed:50).Kph);
    }
    [Fact]
    public void ProjectedPointsDoNotDependOnTheRoutesSamplingDensity()
    {
        var data=Build(Source([Road],Sign("60",400,new(SetKph:60))));
        var sampled=Road with{Points=Enumerable.Range(0,101).Select(i=>P(i*10)).ToArray()};
        Assert.Equal(60,Sim(data,[sampled]).Kph);
    }
    [Fact]
    public void MaximumAndAreaRulesRemainIndependentOfTemporarySimulation()
    {
        var data=Source([Road],Sign("70",100,new(SetKph:70))) with{
            Regions=[new("numeric-area",[P(0,-20),P(1000,-20),P(1000,20),P(0,20)],0,false,40)]};
        Assert.Equal(40,Sim(Build(data),[Road]).Kph);
        data=Source([Road],Sign("150",100,new(SetKph:150)));
        Assert.Equal(120,Sim(Build(data),[Road]).Kph);
    }
    private sealed class SilentAnnouncer:IWarningAnnouncer
    {
        public List<CameraWarning> Warnings { get; }=[];
        public void Announce(CameraWarning warning)=>Warnings.Add(warning);
    }
    [Fact]
    public void RuntimeDoesNotProduceCurrentPositionSpeedComparisons()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        TruckLib.ScsMap.Road.Add(map,P(0),P(1000),default);
        var pose=new VehicleSnapshot(10,0,0,.75,20,1000,true,false,1,now){CurrentSpeedLimitKph=50};
        var game=new GameDataSnapshot(pose,RouteSnapshot.Parse([],now),RouteAvailability.None,"s",true);
        var snapshot=new WarningPipeline(new SilentAnnouncer()).Process(game,new(map,[],[],[]),now);
        Assert.Null(snapshot.CurrentSpeedLimit);
    }
    [Fact]
    public void WarningPipelineAttachesSimulatedLimitOnUniqueRoadWithoutNavigation()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var road=TruckLib.ScsMap.Road.Add(map,P(0),P(1000),default);
        var geometry=R(road.Uid,road.Node.Uid,road.ForwardNode.Uid,P(0),P(1000));
        var limits=Build(Source([geometry],Sign("60",200,new(SetKph:60))));
        CameraObservation camera=new("camera",P(350),P(350),Vector3.UnitX,true,"test.pmd","speedcam.test");
        var nearby=new NearbyMapSnapshot(map,[camera],[],[]){SpeedLimits=limits};
        var pose=new VehicleSnapshot(50,0,0,.75,20,1000,true,false,1,now){CurrentSpeedLimitKph=50};
        var game=new GameDataSnapshot(pose,RouteSnapshot.Parse([],now),RouteAvailability.None,"s",true);
        var announcer=new SilentAnnouncer();var snapshot=new WarningPipeline(announcer).Process(game,nearby,now);
        Assert.Equal(60,Assert.Single(snapshot.Cameras).SpeedLimitKph);
        Assert.Equal(60,Assert.Single(announcer.Warnings).SpeedLimitKph);
        Assert.Null(snapshot.CurrentSpeedLimit);
    }

}
