using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class FerryNavigationTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (NearbyMapSnapshot Map,RouteSnapshot Route,Ferry A,Ferry B,Road Exit) Scene(bool withAreas=true)
    {
        var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,Vector3.Zero,new(0,0,1200),default);
        var outgoing=Road.Add(map,new(10000,0,100),new(10000,0,1000),default);
        var a=Ferry.Add(map,new(30,0,1250),"a");var b=Ferry.Add(map,new(10000,0,0),"b");
        var pa=new Prefab{Nodes=[incoming.ForwardNode],Ferry=a};var pb=new Prefab{Nodes=[outgoing.Node],Ferry=b};
        map.MapItems[pa.Uid]=pa;map.MapItems[pb.Uid]=pb;a.Prefab=pa;b.Prefab=pb;
        var route=RouteSnapshot.Parse(new[]{incoming.Node.Uid,incoming.ForwardNode.Uid,a.Node.Uid,b.Node.Uid,outgoing.Node.Uid,outgoing.ForwardNode.Uid}
            .Select((id,i)=>new RouteNode(id,(5-i)*10000,100)),Now);
        var ferry=new FerryJourneyIndex(map,[("a","b")],new Dictionary<string,string>{{"a","Port A"},{"b","Port B"}},_=>4.5);
        FreeNavigationArea[] areas=[new(pa.Uid.ToString("X16"),[new(-50,0,1200),new(50,0,1200),new(50,0,1300),new(-50,0,1300)],[incoming.ForwardNode.Uid]),
            new(pb.Uid.ToString("X16"),[new(9950,0,-50),new(10050,0,-50),new(10050,0,150),new(9950,0,150)],[outgoing.Node.Uid])];
        return(new(map,[],[],[]){Ferries=ferry,FreeNavigationAreas=withAreas?areas:[]},route,a,b,outgoing);
    }
    private static GameDataSnapshot Data(RouteSnapshot route,float z,DateTimeOffset now,bool paused=false)
        =>new(new(0,0,z,.5,10,1000,true,paused,100,now),route,RouteAvailability.Available,"session",!paused);

    [Theory][InlineData(false)][InlineData(true)]
    public void FerryIsAnActionWithShortFarPreviewsAndFullNearDirection(bool nameClips)
    {
        var scene=Scene();var coordinator=new GuidanceCoordinator();var queued=new List<QueuedGuidanceSpeech>();coordinator.Queued+=queued.Add;
        var runtime=new NavigationRuntime(guidance:coordinator,spokenCityNames:nameClips?new HashSet<string>{"Port A","Port B"}:null);
        foreach(float z in new[]{0f,700,1000,1100,1181})
        {
            var data=Data(scene.Route,z,Now);runtime.Process(data,scene.Map,Now);coordinator.Tick(data,Now);
        }
        Assert.Contains(queued,q=>q.Text=="五百米后，进入码头。");
        Assert.Contains(queued,q=>q.Text=="两百米后，进入码头。");
        string full=nameClips?"进入Port A码头，乘坐前往Port B的轮渡":FerryNavigationPrompts.Generic;
        Assert.Contains(queued,q=>q.Text=="一百米后，"+full+"。");Assert.Contains(queued,q=>q.Text==full+"。");
        Assert.DoesNotContain(queued,q=>q.Text==NavigationPrompts.Continue());
        Assert.Equal(1,runtime.StructureBuildCount);Assert.Equal(1,runtime.EventPlanBuildCount);
    }

    [Fact]
    public void EnteringTheKnownFreeAreaEndsTheSeriesWithoutInventingAnInternalRoadDistance()
    {
        var scene=Scene();var coordinator=new GuidanceCoordinator();var runtime=new NavigationRuntime(guidance:coordinator);
        var before=Data(scene.Route,1181,Now);runtime.Process(before,scene.Map,Now);coordinator.Tick(before,Now);
        var series=Assert.Single(coordinator.Timeline.Sources.Single(p=>p.Source=="navigation").Sequences);
        var entered=Data(scene.Route,1201,Now.AddSeconds(.2));var result=runtime.Process(entered,scene.Map,Now.AddSeconds(.2));coordinator.Tick(entered,Now.AddSeconds(.2));
        Assert.Contains("未唯一匹配",result.State);Assert.NotNull(runtime.ExpectedFerry);
        Assert.True(coordinator.Scheduler.IsConsumed("navigation",SpeechSequence.EndTriggerId(series.Id)));
        Assert.Contains(coordinator.Diagnostic.LatestTriggers,r=>r.Disposition==TriggerDisposition.Ended);
        // Booking can trim away both activation records while placement is still in the departure port.
        var booked=RouteSnapshot.Parse([new(scene.Exit.Node.Uid,1000,100),new(scene.Exit.ForwardNode.Uid,0,0)],Now);
        runtime.Process(Data(booked,1250,Now.AddSeconds(.4),paused:true),scene.Map,Now.AddSeconds(.4));
        Assert.NotNull(runtime.ExpectedFerry);Assert.Equal("b",runtime.ExpectedFerry.Leg.Target.Id);
    }

    [Fact]
    public void AChangedGoalDoesNotKeepAnEnteredPortExpectationAlive()
    {
        var scene=Scene();var runtime=new NavigationRuntime();runtime.Process(Data(scene.Route,1201,Now),scene.Map,Now);
        Assert.NotNull(runtime.ExpectedFerry);
        var changed=RouteSnapshot.Parse([new(scene.Exit.Node.Uid,1000,100),new(999,0,0)],Now);
        runtime.Process(Data(changed,1250,Now),scene.Map,Now);Assert.Null(runtime.ExpectedFerry);
    }

    [Fact]
    public void NativeEntranceRequiresContinuousApproachAndRejectsParallelRoadAndTeleport()
    {
        var entrance=new FerryEntrance(new(0,0,100),Vector3.UnitZ,4.5,
            new("road",1,2,[Vector3.Zero,new(0,0,100)]));
        VehicleSnapshot P(double x,double z,double t)=>new(x,0,z,.5,10,1000,true,false,100,Now.AddSeconds(t));
        Assert.True(entrance.Crossed(P(2,99,0),P(2,101,.2),true));
        Assert.False(entrance.Crossed(P(20,99,0),P(20,101,.2),true));
        Assert.False(entrance.Crossed(P(0,0,0),P(0,200,.2),true));
        Assert.True(entrance.Crossed(P(2,101,0),P(2,99,.2),false));
    }

    [Fact]
    public void AnOutlineLessPortUsesItsRealInterfaceAndClearsWhenDrivingBackOut()
    {
        var scene=Scene(withAreas:false);var runtime=new NavigationRuntime();
        runtime.Process(Data(scene.Route,1199,Now),scene.Map,Now);
        runtime.Process(Data(scene.Route,1201,Now.AddSeconds(.2)),scene.Map,Now.AddSeconds(.2));
        Assert.NotNull(runtime.ExpectedFerry);
        runtime.Process(Data(scene.Route,1199,Now.AddSeconds(.4)),scene.Map,Now.AddSeconds(.4));
        Assert.Null(runtime.ExpectedFerry);
    }

    [Fact]
    public void PartialNameAvailabilityNeverSendsAnUncachedForeignPlaceToTheLocalVoice()
        =>Assert.Equal(FerryNavigationPrompts.Generic,FerryNavigationPrompts.Passage("Port A","Port B",new HashSet<string>{"Port A"}));
}
