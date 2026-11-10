using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class SpeedLimitTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (TruckLib.ScsMap.Map Map,Road Road,SpeedLimitData Data) Scene(params LimitSign[] signs)
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);
        var geometry=new DirectedRoad(road.Uid.ToString("X16"),road.Node.Uid,road.ForwardNode.Uid,
            Enumerable.Range(0,201).Select(i=>new Vector3(i*5,0,0)).ToArray());
        return(map,road,new(new Dictionary<ulong,LimitRoad>{{road.Uid,new(geometry,"local_road")}},signs,[],
            new Dictionary<string,TruckSpeedLimit>{{"norway/local_road",new(80,50,80)}},"norway"));
    }
    private static LimitSign Sign(string id,float x,LimitAction action,bool reverse=false)
        =>new(id,new(x,0,5),reverse?-Vector3.UnitX:Vector3.UnitX,false,action);
    private static SpeedLimitResult Resolve((TruckLib.ScsMap.Map Map,Road Road,SpeedLimitData Data) scene)
        =>new SpeedLimitResolver(scene.Data,new RoadPathSource(scene.Map)).Resolve(new("camera",new(700,0,0),Vector3.UnitX));

    [Fact]
    public void DirectionalClassSelectsActualTruckRuleAtCamera()
    {
        var s=Scene();
        s.Data=s.Data with { Limits=new Dictionary<string,TruckSpeedLimit> {
            ["norway/local_road"]=new(60,50,60),["norway/motorway"]=new(80,80,80) },
            Roads=new Dictionary<ulong,LimitRoad> { [s.Road.Uid]=s.Data.Roads[s.Road.Uid] with {
                DirectionalClasses=new("motorway","local_road") } } };
        Assert.Equal(80,Resolve(s).Kph);
        var reverse=new SpeedLimitResolver(s.Data,new RoadPathSource(s.Map))
            .Resolve(new("opposite",new(300,0,0),-Vector3.UnitX));
        Assert.Equal(60,reverse.Kph);
    }

    [Fact]
    public void UncertainRoadCountryCannotBeOverriddenByCityOrGlobalFallback()
    {
        var s=Scene();
        s.Data=s.Data with { Roads=new Dictionary<ulong,LimitRoad>{
            [s.Road.Uid]=s.Data.Roads[s.Road.Uid] with { CountryUncertain=true } },
            Regions=[new("city",[new(650,0,-100),new(1000,0,-100),new(1000,0,100),new(650,0,100)],0,true){Country="norway"}] };
        Assert.Null(Resolve(s).Kph);
    }
    [Fact]
    public void RoadCountryResolvesRuralLimitWithoutMapWindowCountry()
    {
        var s=Scene();
        s.Data=s.Data with { Country=null, Roads=new Dictionary<ulong,LimitRoad>{
            [s.Road.Uid]=s.Data.Roads[s.Road.Uid] with { Country="norway" } } };
        Assert.Equal(80,Resolve(s).Kph);
    }

    [Fact]
    public void ConflictingRoadAndContainingCityCountriesRemainUnknown()
    {
        var s=Scene();
        s.Data=s.Data with { Country=null, Roads=new Dictionary<ulong,LimitRoad>{
            [s.Road.Uid]=s.Data.Roads[s.Road.Uid] with { Country="norway" } },
            Regions=[new("foreign-city",[new(650,0,-100),new(1000,0,-100),new(1000,0,100),new(650,0,100)],0,true){Country="finland"}] };
        Assert.Null(Resolve(s).Kph);
    }

    [Fact] public void DefaultRuralAndCityLimitsUseCameraPosition()
    {
        var s=Scene();
        Assert.Equal(80,Resolve(s).Kph);
        s.Data=s.Data with{Regions=[new("city",[new(650,0,-100),new(1000,0,-100),new(1000,0,100),new(650,0,100)],0,true)]};
        Assert.Equal(50,Resolve(s).Kph);
    }
    [Fact] public void OrderedSameDirectionSignsApplyAndOppositeSignIsIgnored()
        =>Assert.Equal(60,Resolve(Scene(Sign("a",100,new(SetKph:70)),Sign("b",300,new(SetKph:60)),
            Sign("opposite",500,new(SetKph:30),true))).Kph);
    [Fact] public void EndSignRestoresDefaultRatherThanUnlimited()
        =>Assert.Equal(80,Resolve(Scene(Sign("a",100,new(SetKph:50)),Sign("end",500,new(Reset:true)))).Kph);
    [Fact] public void TruckMaximumCapsPostedLimit()
        =>Assert.Equal(80,Resolve(Scene(Sign("a",100,new(SetKph:100)))).Kph);
    [Fact] public void TemplateOverrideAndAmbiguousOwnershipStayUnknown()
    {
        Assert.Null(Resolve(Scene(Sign("a",200,new(SetKph:50)) with{TemplateOverride=true})).Kph);
        Assert.Null(Resolve(Scene(Sign("a",200,new(SetKph:50)) with{OwnershipAmbiguous=true})).Kph);
    }
    [Fact] public void ANewKnownSignCanRestoreStateAfterUnsupportedRule()
        =>Assert.Equal(60,Resolve(Scene(Sign("unknown",100,new(Unsupported:true)),Sign("known",400,new(SetKph:60)))).Kph);
    [Fact] public void MissingCountryDoesNotGuess()
    {
        var s=Scene();s.Data=s.Data with{Country=null};
        Assert.Null(Resolve(s).Kph);
    }
    [Fact] public void RegionHeightDoesNotAffectOverpass()
    {
        var s=Scene();s.Data=s.Data with{Regions=[new("zone",[new(600,10,-20),new(800,10,-20),new(800,10,20),new(600,10,20)],3,false,30)]};
        Assert.Equal(80,Resolve(s).Kph);
    }
    [Fact] public void SpeedLimitIsCarriedThroughPlanAndAlert()
    {
        var e=new CameraWarningEngine();
        var data=new WarningSnapshot("s","r",true,true,Now,[new("camera",CameraApproach.RouteAhead,499,499){SpeedLimitKph=50}]);
        Assert.All(e.GetPlans(data,Now), p=>Assert.Equal(50,p.SpeedLimitKph));
        Assert.Equal(50,Assert.Single(e.Update(data,Now)).SpeedLimitKph);
    }
    [Theory]
    [InlineData(500,499,false,50,"五百米处，有测速照相，限速五十。")]
    [InlineData(200,199,false,80,"两百米处，有测速照相，限速八十。")]
    [InlineData(200,150,true,65,"两百米内，有测速照相，限速六十五。")]
    public void SpeechUsesCameraLimit(double stage,double distance,bool late,int limit,string text)
        =>Assert.Equal(text,PlannedSpeechAnnouncer.Text(stage,distance,late,limit));
    [Fact] public void CurrentLimitLoggingDoesNotRepeatAsVehicleMoves()
    {
        var messages=new List<string>();var log=new NavigationLogReporter(messages.Add);
        for(int i=0;i<10;i++){
            var pose=new VehicleSnapshot(i,0,0,0,10,1000,true,false,100,Now){CurrentSpeedLimitKph=80};
            log.ReportCurrentLimit(new(pose,RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true),Now);
        }
        Assert.Single(messages);
        var changed=new VehicleSnapshot(100,0,0,0,10,1000,true,false,100,Now){CurrentSpeedLimitKph=50};
        log.ReportCurrentLimit(new(changed,RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true),Now);
        Assert.Equal(2,messages.Count);
    }

    [Fact] public void PostedLimitSurvivesDefaultTransitionAndCanRaiseUrbanDefault()
    {
        var s=Scene(Sign("posted",200,new(SetKph:70)));
        s.Data=s.Data with{Regions=[new("city",[new(650,0,-100),new(1000,0,-100),new(1000,0,100),new(650,0,100)],0,true)]};
        Assert.Equal(70,Resolve(s).Kph);
    }
    private sealed class NoSynthesis : ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text,CancellationToken cancellationToken)=>throw new InvalidOperationException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class NoFallback : IWarningAnnouncer
    { public void Announce(CameraWarning warning)=>throw new InvalidOperationException("Unexpected beep."); }
    [Fact] public async Task UnreadyLimitPhraseUsesMatchingCachedBasicPhraseInsteadOfBeep()
    {
        string path=Path.Combine(Path.GetTempPath(),"nav-limit-voice-"+Guid.NewGuid().ToString("N")+".wav");
        File.WriteAllBytes(path,[1]);
        try
        {
            var played=new List<string>();
            await using var speech=new PlannedSpeechAnnouncer(new NoSynthesis(),new NoFallback(),played.Add,
                cancellationToken:TestContext.Current.CancellationToken,
                preGenerated:new Dictionary<string,string>{{"五百米处，有测速照相。",path}});
            speech.Announce(new("camera",499,WarningDistanceKind.Road){StageDistanceMeters=500,SpeedLimitKph=50});
            Assert.Equal(path,Assert.Single(played));
        }
        finally{File.Delete(path);}
    }

}
