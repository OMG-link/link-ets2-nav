using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationRuntimeTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (NearbyMapSnapshot Map,RouteSnapshot Route) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var incoming=Road.Add(map,new(0,0,0),new(0,0,700),default);
        var right=Road.Add(map,new(-30,0,730),new(-600,0,730),default);
        var straight=Road.Add(map,new(0,0,760),new(0,0,1300),default);
        DirectedRoad[] links=[
            new("prefab:ABC:0:1",incoming.ForwardNode.Uid,right.Node.Uid,
                [new(0,0,700),new(0,0,710),new(-10,0,725),new(-30,0,730)]),
            new("prefab:ABC:0:2",incoming.ForwardNode.Uid,straight.Node.Uid,[new(0,0,700),new(0,0,760)])];
        var route=RouteSnapshot.Parse([new(incoming.Node.Uid,1300,60),
            new(incoming.ForwardNode.Uid,600,30),new(right.Node.Uid,570,28),new(right.ForwardNode.Uid,0,0)],Now);
        return(new(map,[],[],[]){PrefabPaths=links},route);
    }
    private static GameDataSnapshot Data(RouteSnapshot route,float z,RouteAvailability availability=RouteAvailability.Available,
        bool paused=false,bool stale=false)=>new(
        new(0,0,z,.5,20,1000,true,paused,1,stale?Now.AddSeconds(-3):Now),
        route,availability,"session",!paused);
    [Fact]
    public void OfficialRouteProducesTwoRightTurnStagesWithoutCameraOrLaneData()
    {
        var scene=Scene();var calls=0;var logs=new List<string>();
        var runtime=new NavigationRuntime(fallback:()=>calls++,log:logs.Add);
        var next=runtime.Process(Data(scene.Route,300),scene.Map,Now);
        Assert.Equal(ManeuverKind.TurnRight,next.Next!.Kind);
        Assert.InRange(next.Next.DistanceMeters,399,401);
        runtime.Process(Data(scene.Route,401),scene.Map,Now);
        runtime.Process(Data(scene.Route,403),scene.Map,Now);
        runtime.Process(Data(scene.Route,651),scene.Map,Now);
        runtime.Process(Data(scene.Route,653),scene.Map,Now);
        Assert.Equal(2,calls);
        Assert.Contains(logs,l=>l.Contains("导航指令：前方三百米，右转。"));
        Assert.Contains(logs,l=>l.Contains("导航指令：前方五十米，右转。"));
    }
    [Fact]
    public void MissingNavigationPauseAndStalePoseNeverAnnounce()
    {
        var scene=Scene();var calls=0;
        var runtime=new NavigationRuntime(fallback:()=>calls++);
        Assert.Null(runtime.Process(Data(scene.Route,500,RouteAvailability.None),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,paused:true),scene.Map,Now).Next);
        Assert.Null(runtime.Process(Data(scene.Route,500,stale:true),scene.Map,Now).Next);
        Assert.Equal(0,calls);
    }
    [Fact]
    public void EveryRuntimePhraseExistsInPreGeneratedPack()
    {
        Assert.Equal(18,NavigationPrompts.AllTexts().Distinct().Count());
        foreach(string text in NavigationPrompts.AllTexts())
            Assert.Contains(text,LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
    }
}
