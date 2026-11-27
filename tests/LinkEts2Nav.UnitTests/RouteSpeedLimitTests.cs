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
        roads.ToDictionary(r=>Convert.ToUInt64(r.Id,16),r=>new LimitRoad(r,"local_road"){Country="test"}),signs,[],
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
        var resolver=new SpeedLimitResolver(data,new RoadPathSource(new TruckLib.ScsMap.Map()));
        MonitoredCamera camera=new("camera",P(350),Vector3.UnitX);
        Assert.Equal(50,resolver.ResolveOnPath(P(0),Vector3.UnitX,50,camera,[a,c]).Kph);
        Assert.Equal(80,resolver.ResolveOnPath(P(0,30),Vector3.UnitX,50,camera,[b,c]).Kph);
        Assert.Equal(50,resolver.ResolveOnPath(P(0),Vector3.UnitX,50,camera,[a,c]).Kph);
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
        var resolver=new SpeedLimitResolver(data,new RoadPathSource(new TruckLib.ScsMap.Map()));
        MonitoredCamera camera=new("camera",P(900),Vector3.UnitX);
        Assert.Equal(50,resolver.ResolveOnPath(P(0),Vector3.UnitX,50,camera,[Road]).Kph);
        Assert.Equal(80,resolver.ResolveOnPath(P(0),Vector3.UnitX,80,camera,[Road]).Kph);
        Assert.Equal(50,resolver.ResolveOnPath(P(0),Vector3.UnitX,50,camera,[Road]).Kph);
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
    public void MissingProjectionHasTypedFailureInsteadOfGuessingASector()
    {
        var result=Sim(Build(Source([])),[Road]);
        Assert.Null(result.Kph);
        Assert.Equal(MapQueryFailureKind.DefinitionUnavailable,Assert.IsType<MapQueryFailure>(result.MapIssue).Kind);
        Assert.Equal(P(900),result.MapIssue!.QueryAnchor);
    }
    [Fact]
    public void ProjectionOwnerEvidenceCanSupplyAnExactScopedRetry()
    {
        var required=new MapObjectKey(MapObjectKind.Item,83);
        var reported=new List<MapQueryIssue>();
        var source=new RoadPathSource(new TruckLib.ScsMap.Map(),reportMapIssue:reported.Add);
        var resolver=new SpeedLimitResolver(Build(Source([])),source,
            _=>new MissingMapObjects([required],"known metadata owner") with{QueryAnchor=P(50000)});
        var result=resolver.ResolveOnPath(P(0),Vector3.UnitX,80,new("camera",P(900),Vector3.UnitX),[Road]);
        Assert.Equal([required],Assert.IsType<MissingMapObjects>(result.MapIssue).Keys);
        Assert.Equal(P(900),result.MapIssue!.QueryAnchor);
        Assert.Contains(result.MapIssue,reported);
    }
    [Fact]
    public void NativePrefabSignsAreReadInTheChosenRouteOrder()
    {
        var a=R(1,10,11,P(0),P(100));var b=R(2,12,13,P(200),P(400));
        DirectedRoad bridge=new("prefab:ABC:0:1",11,12,[P(100),P(200)]);
        var data=Build(Source([a,b],Sign("60",150,new(SetKph:60))),bridge);
        Assert.Equal(60,Sim(data,[a,bridge,b],target:P(350)).Kph);
    }
    [Theory]
    [InlineData(20,55,80)]
    [InlineData(20,78,80)]
    [InlineData(20,110,30)]
    [InlineData(60,55,80)]
    [InlineData(60,78,50)]
    [InlineData(60,110,50)]
    public void ObservedNativeReturnReadsOnlySignsOnItsBranch(int branchZ,int cameraX,int expected)
    {
        var edge=ReturnBranches(60);
        var observed=edge.LanePaths[branchZ==20?0:1];
        var route=edge with{Points=observed.Points,ObservedLane=observed};
        var approach=R(1,9,10,P(-100),P(0));
        var data=Build(Source([approach],
            new("near-30",P(92,23),Vector3.UnitX,false,new(SetKph:30)),
            new("far-50",P(68,63),Vector3.UnitX,false,new(SetKph:50))),edge);

        Assert.Equal(expected,Sim(data,[route],start:P(38,branchZ),target:P(cameraX,branchZ)).Kph);
    }

    [Theory]
    [InlineData(32)] // Both branches are within the sign installation radius.
    [InlineData(60)]
    public void ObservedBranchKeepsItsSignStationAndTheSharedEntrySign(int farZ)
    {
        var edge=ReturnBranches(farZ);
        var far=edge.LanePaths[1];var near=edge.LanePaths[0];
        var farVisit=edge with{Points=far.Points,ObservedLane=far};
        var nearVisit=edge with{Points=near.Points,ObservedLane=near};
        var data=Build(Source([],new("shared",P(10,2),Vector3.UnitX,false,new(Reset:true)),
            new("far-entry",P(68,farZ+3),Vector3.UnitX,false,new(Urban:true)),
            new("unconfirmed-template",P(90,farZ+3),Vector3.UnitX,false,new(SetKph:50),TemplateOverride:true),
            new("faces-away",P(100,farZ+3),-Vector3.UnitX,false,new(SetKph:30)),
            new("overhead",new(110,6,farZ),Vector3.UnitX,false,new(SetKph:30))),edge);

        var index=data.SignProjections!;
        Assert.Equal(["shared"],index.On(nearVisit).Select(s=>s.SignId));
        Assert.Equal(["shared","far-entry","unconfirmed-template"],index.On(farVisit).Select(s=>s.SignId));
        var entry=index.On(farVisit)[1];
        Assert.Equal(P(68,farZ),entry.Position);
        Assert.Equal(farZ+68,entry.AlongMeters);
        Assert.True(index.On(farVisit)[2].Action.Unsupported);
        Assert.Empty(new CityEntryEventBuilder().Build([nearVisit],0,data));
        Assert.Equal(farZ+68,Assert.Single(new CityEntryEventBuilder().Build([farVisit],0,data)).AlongMeters);

        // An independently rebuilt map may reuse every numeric ID and native curve number.
        var otherSnapshot=farVisit with{ObservedLane=far with{}};
        Assert.False(index.Contains(otherSnapshot));
        Assert.Empty(index.On(otherSnapshot));
    }

    private static DirectedRoad ReturnBranches(int farZ)
    {
        static PrefabLanePath Lane(int slot,int z)
        {
            Vector3[] points=[P(0),P(20),P(20,z),P(140,z),P(140,-20),P(0,-20),P(0,-3)];
            return new(0xABC,10,10,slot,slot,slot,slot,2,2,[slot],points,
                points.Zip(points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second)));
        }
        var near=Lane(0,20);var far=Lane(1,farZ);
        return new("prefab:ABC:0:0",10,10,near.Points){LanePaths=[near,far],ControlPositions=(P(0),P(0))};
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
        data=Source([Road],Sign("150",100,new(SetKph:150))) with{
            Limits=new Dictionary<string,TruckSpeedLimit>{{"test/local_road",new(160,50,120)}}};
        Assert.Equal(120,Sim(Build(data),[Road]).Kph);
    }
    [Fact]
    public void RuralSdkSeedIsCappedByUrbanAreaAtCameraBeforePlayerEntersIt()
    {
        var data=Source([Road]) with{
            Regions=[new("urban",[P(650,-20),P(1000,-20),P(1000,20),P(650,20)],0,true)]};
        var result=Sim(Build(data),[Road],seed:80);
        Assert.Equal(50,result.Kph);
        Assert.Contains("SDK 当前限速初始化",result.Reason);
        Assert.Contains("城区默认限速",result.Reason);
    }
    [Theory]
    [InlineData(40,80,60,120,40)]
    [InlineData(100,80,90,120,90)]
    [InlineData(100,110,60,120,60)]
    [InlineData(150,160,140,120,120)]
    public void SameRoadPostedLimitIsSubjectToAreaAndTruckCaps(int posted,int rural,int area,int maximum,int expected)
    {
        var data=Source([Road],Sign("posted",100,new(SetKph:posted))) with{
            Limits=new Dictionary<string,TruckSpeedLimit>{{"test/local_road",new(rural,50,maximum)}},
            Regions=[new("numeric",[P(650,-20),P(1000,-20),P(1000,20),P(650,20)],0,false,area)]};
        Assert.Equal(expected,Sim(Build(data),[Road]).Kph);
    }

    [Theory]
    [InlineData(0,60)]
    [InlineData(490,60)]
    [InlineData(810,80)]
    public void SwedishHundredSignAndTruckSeedBothGiveEightyAtTheCamera(float start,double sdk)
    {
        var a=R(1,10,11,P(0),P(850));var b=R(2,11,12,P(850),P(1100));
        var data=Source([a,b],Sign("limit_100",800,new(SetKph:100))) with
            { Limits=new Dictionary<string,TruckSpeedLimit>{{"test/local_road",new(70,50,80)}} };
        Assert.Equal(80,Sim(Build(data),[a,b],start:P(start),target:P(920),seed:sdk).Kph);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighSdkSeedCannotCrossAnotherRoadClassOrCountryAndReturn(bool countryChange)
    {
        var a=R(1,10,11,P(0),P(300));var b=R(2,11,12,P(300),P(600));var c=R(3,12,13,P(600),P(1000));
        var data=Source([a,b,c]);var roads=data.Roads.ToDictionary();
        roads[2]=countryChange?roads[2] with{Country="other"}:roads[2] with{SpeedClass="expressway"};
        Assert.Equal(80,Sim(Build(data with{Roads=roads}),[a,b,c],seed:100).Kph);
    }

    [Theory]
    [InlineData(498,70)]
    [InlineData(506,50)]
    public void HigherUrbanSignSurvivesContinuousCoverageButNotAGap(float nextCityStarts,int expected)
    {
        static LimitRegion Urban(string id,float left,float right) =>
            new(id,[P(left,-20),P(right,-20),P(right,20),P(left,20)],0,true);
        var data=Source([Road],Sign("posted",200,new(SetKph:70))) with
            {Regions=[Urban("city-a",100,500),Urban("city-b",nextCityStarts,1000)]};
        Assert.Equal(expected,Sim(Build(data),[Road]).Kph);
    }

    [Fact]
    public void SignAfterAPolylineBreakUsesOnlyDrivenSegmentsAndKeepsItsPhysicalPosition()
    {
        var split=R(1,10,11,P(0),P(60),P(100),P(400),P(500)) with{SegmentStarts=[3]};
        var data=Build(Source([split],Sign("in-the-gap",250,new(SetKph:10)),Sign("posted",400,new(SetKph:60))));
        var projected=Assert.Single(data.SignProjections!.On(split));
        Assert.Equal("posted",projected.SignId);
        Assert.Equal(100,projected.AlongMeters);
        Assert.Equal(P(400),projected.Position);
        Assert.Equal(60,Sim(data,[split],target:P(475)).Kph);
    }
    [Fact]
    public void CancellationClearsThePostedValueAndRestoresUrbanDefaultAtCamera()
    {
        var data=Source([Road],Sign("posted",100,new(SetKph:30)),Sign("cancel",400,new(Reset:true))) with{
            Regions=[new("urban",[P(650,-20),P(1000,-20),P(1000,20),P(650,20)],0,true)]};
        Assert.Equal(30,Sim(Build(data),[Road],target:P(350)).Kph);
        Assert.Equal(50,Sim(Build(data),[Road]).Kph);
    }
    [Fact]
    public void MissingSlowRoadUsesThirtyForTheActualCameraPath()
    {
        var data=Source([Road]);
        data=data with{Roads=data.Roads.ToDictionary(p=>p.Key,p=>p.Value with{SpeedClass="slow_road"})};
        var result=Sim(Build(data),[Road],seed:80);
        Assert.Equal(30,result.Kph);Assert.Contains(TruckSpeedLimitRules.DefaultReason,result.Reason);
        data=data with{Limits=new Dictionary<string,TruckSpeedLimit>{{"test/slow_road",new(45,45,45)}}};
        Assert.Equal(45,Sim(Build(data),[Road],seed:80).Kph);
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
        var snapshot=new WarningPipeline().Process(game,new(map,[],[],[]),now);
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
        var recorder=new RecordedWarningPipeline();var snapshot=recorder.Process(game,nearby,now);
        Assert.Equal(60,Assert.Single(snapshot.Cameras).SpeedLimitKph);
        Assert.Contains("限速六十",Assert.Single(recorder.Queued).Text);
        Assert.Null(snapshot.CurrentSpeedLimit);
    }

}
