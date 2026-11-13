using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class CityEntryEventBuilderTests
{
    private static readonly DirectedRoad Road=new("000000000000000A",1,2,[new(0,0,0),new(1500,0,0)]);
    private static LimitSign Sign(string id,float x,bool urban=true,bool reverse=false)
        =>new(id,new(x,0,-2),reverse?-Vector3.UnitX:Vector3.UnitX,false,new(Urban:urban));
    private static SpeedLimitData Data(params LimitSign[] signs)=>new(new Dictionary<ulong,LimitRoad>{{10,new(Road,"local_road")}},
        signs,[],new Dictionary<string,TruckSpeedLimit>(),null);
    private static IReadOnlyList<EnvironmentPreviewEvent> Events(SpeedLimitData data,double player=0)
        =>new CityEntryEventBuilder().Build([Road],player,ProjectedSignBuilder.Build(data,[],TestContext.Current.CancellationToken));

    [Fact] public void SignPositionIsTheEventAndEndWithOrdinaryFiveHundredTwoHundredProfile()
    {
        var e=Assert.Single(Events(Data(Sign("entry",600))));
        Assert.Equal(600,e.AlongMeters);Assert.Equal(600,e.EndAlongMeters);Assert.Equal("city-entry",e.Kind);
        var plan=new EnvironmentPreviewPlanner().Build("city",0,[e],new SpeechTriggerScheduler(),true);
        Assert.Equal(new[]{100d,400d},plan.Triggers.Select(t=>t.AlongMeters));
        Assert.Equal(new[]{"五百米后，进入城区，请注意减速慢行。","两百米后，进入城区，请注意减速慢行。"},plan.Triggers.Select(t=>t.Text));
    }
    [Fact] public void OppositeEntryAndExitDoNotGenerateEntryOnTheWrongDirectedRoad()
    {
        var data=ProjectedSignBuilder.Build(Data(Sign("entry",600),Sign("exit",600,false,true)),[],TestContext.Current.CancellationToken);
        Assert.Single(new CityEntryEventBuilder().Build([Road],0,data));
        var reverse=Road with{StartNodeUid=2,EndNodeUid=1,Points=Road.Points.Reverse().ToArray()};
        Assert.Empty(new CityEntryEventBuilder().Build([reverse],0,data));
        Assert.Empty(Events(Data(Sign("exit-only",600,false))));
    }
    [Fact] public void RouteWithoutTheProjectedDirectedEdgeDoesNotSeeTheSign()
    {
        var data=ProjectedSignBuilder.Build(Data(Sign("entry",600)),[],TestContext.Current.CancellationToken);
        Assert.Empty(new CityEntryEventBuilder().Build([Road with{Id="other",StartNodeUid=3,EndNodeUid=4}],0,data));
    }
    [Fact] public void PolygonMembershipAloneNeverCreatesAnAnnouncement()
    {
        var urban=new LimitRegion("city",[new(400,0,-20),new(900,0,-20),new(900,0,20),new(400,0,20)],0,true);
        Assert.Empty(Events(Data() with{Regions=[urban]}));
        Assert.Empty(Events(Data() with{Regions=[urban]},500));
    }
    [Fact] public void ContinuousEntriesAreDeduplicatedUntilARealExitSign()
    {
        var e=Events(Data(Sign("entry",600),Sign("parallel-board",600.005f),Sign("repeat",800),
            Sign("exit",900,false),Sign("new-entry",1000)));
        Assert.Equal(new[]{600d,1000d},e.Select(s=>s.AlongMeters));Assert.NotEqual(e[0].Id,e[1].Id);
    }
    [Fact] public void ActuallyPassedStateSurvivesSlidingMapWindows()
    {
        var builder=new CityEntryEventBuilder();var full=ProjectedSignBuilder.Build(Data(Sign("entry",600),Sign("repeat",900)),[],TestContext.Current.CancellationToken);
        Assert.Single(builder.Build([Road],0,full));
        Assert.Empty(builder.Build([Road],650,full));
        var slide=ProjectedSignBuilder.Build(Data(Sign("repeat",900)),[],TestContext.Current.CancellationToken);
        Assert.Empty(builder.Build([Road],700,slide));
        var exit=ProjectedSignBuilder.Build(Data(Sign("exit",800,false),Sign("repeat",900)),[],TestContext.Current.CancellationToken);
        Assert.Single(builder.Build([Road],700,exit));
    }
    [Fact] public void ReplacingAParallelBoardBeforeArrivalKeepsTheSameLandmarkIdentity()
    {
        var builder=new CityEntryEventBuilder();
        var first=Assert.Single(builder.Build([Road],0,Data(Sign("first-board",600))));
        var second=Assert.Single(builder.Build([Road],100,Data(Sign("parallel-board",600.005f))));
        Assert.Equal(first.Id,second.Id);
    }
    [Fact] public void FutureForecastDoesNotChangeActualUrbanState()
    {
        var builder=new CityEntryEventBuilder();
        Assert.Single(builder.Build([Road],0,Data(Sign("future",600))));
        Assert.Single(builder.Build([Road],100,Data(Sign("new-future",900))));
    }
    [Fact] public void FallbackProjectionRebuildsWhenPrefabGeometryChangesOnTheSameData()
    {
        var builder=new CityEntryEventBuilder();var data=Data(Sign("entry",600));
        IReadOnlyList<DirectedRoad> empty=[];
        Assert.Single(builder.Build([Road],0,data,0,empty));
        DirectedRoad[] competing=[new("prefab:ABC:0:1",3,4,[new(0,0,.2f),new(1500,0,.2f)])];
        Assert.Empty(builder.Build([Road],0,data,0,competing));
    }
    [Fact] public void CachedWorldPointIsReprojectedForDifferentRouteSampling()
    {
        var coarse=Road with{Points=[new(0,0,0),new(500,0,100),new(1500,0,100)]};
        var data=new SpeedLimitData(new Dictionary<ulong,LimitRoad>{{10,new(coarse,"local_road")}},
            [new("entry",new(500,0,102),Vector3.UnitX,false,new(Urban:true))],[],new Dictionary<string,TruckSpeedLimit>(),null);
        data=ProjectedSignBuilder.Build(data,[],TestContext.Current.CancellationToken);
        var dense=coarse with{Points=[new(0,0,0),new(250,0,90),new(500,0,100),new(1500,0,100)]};
        var e=Assert.Single(new CityEntryEventBuilder().Build([dense],0,data));
        double expected=(double)Vector3.Distance(dense.Points[0],dense.Points[1])+Vector3.Distance(dense.Points[1],dense.Points[2]);
        Assert.Equal(expected,e.AlongMeters,3);
        Assert.NotEqual(Assert.Single(data.SignProjections!.On(coarse)).AlongMeters,e.AlongMeters);
    }
    [Fact] public void RebasePreservesActualStateButSessionResetClearsIt()
    {
        var builder=new CityEntryEventBuilder();builder.Build([Road],650,Data(Sign("passed",600)));
        builder.Rebase();Assert.Empty(builder.Build([Road],0,Data(Sign("repeat",900))));
        builder.Reset();Assert.Single(builder.Build([Road],0,Data(Sign("repeat",900))));
    }
    [Fact] public void InitialActivationAfterEntryDoesNotReplayIt()
        =>Assert.Empty(Events(Data(Sign("passed-entry",600),Sign("repeat",900)),650));

    [Fact] public void TrimmedRoutePrefixKeepsTheSameCanonicalLandmark()
    {
        var first=new DirectedRoad("000000000000000B",1,2,[new(0,0,0),new(500,0,0)]);
        var second=new DirectedRoad("000000000000000C",2,3,[new(500,0,0),new(1500,0,0)]);
        var data=new SpeedLimitData(new Dictionary<ulong,LimitRoad>{{11,new(first,"local_road")},{12,new(second,"local_road")}},
            [Sign("entry",600)],[],new Dictionary<string,TruckSpeedLimit>(),null);
        data=ProjectedSignBuilder.Build(data,[],TestContext.Current.CancellationToken);var builder=new CityEntryEventBuilder();
        var full=Assert.Single(builder.Build([first,second],350,data));
        var trimmed=Assert.Single(builder.Build([second],550,data,500));
        Assert.Equal(600,trimmed.AlongMeters);Assert.Equal(full.Id,trimmed.Id);
    }
    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public void TemplateAndOwnershipUncertaintyAreNotBypassedOrLoggedEveryFrame(bool template,bool ownership)
    {
        var lines=new List<string>();var builder=new CityEntryEventBuilder(lines.Add);
        var data=ProjectedSignBuilder.Build(Data(Sign("uncertain",600) with{TemplateOverride=template,OwnershipAmbiguous=ownership}),[],TestContext.Current.CancellationToken);
        Assert.Empty(builder.Build([Road],0,data));Assert.Empty(builder.Build([Road],0,data));
        Assert.Single(lines);Assert.Contains("未确认",lines[0]);
    }
    [Fact] public void UncertainExitDoesNotFalselyProveThatUrbanStatePersists()
    {
        var e=Events(Data(Sign("entry",600),Sign("uncertain-exit",800,false) with{TemplateOverride=true},Sign("next-entry",1000)));
        Assert.Equal(new[]{600d,1000d},e.Select(s=>s.AlongMeters));
    }
    [Theory]
    [InlineData(450,"四百五十米后，进入城区，请注意减速慢行。")]
    [InlineData(225,"两百米后，进入城区，请注意减速慢行。")]
    [InlineData(25,"即将进入城区，请注意减速慢行。")]
    public void LateEntryFloorsToFiftyAndDoesNotKeepTwoHundred(double distance,string text)
    {
        var e=Assert.Single(Events(Data(Sign("entry",600)),600-distance));
        var plan=new EnvironmentPreviewPlanner().Build("city",600-distance,[e],new SpeechTriggerScheduler(),true);
        Assert.Equal(text,Assert.Single(plan.Triggers).Text);
    }
}
