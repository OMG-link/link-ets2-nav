using LinkEts2Nav.Map;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
using System.Numerics;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class MapIdentifierTests
{
    [Fact]
    public void DestinationRequiresUniqueExplicitParentCompanyAndNeverUsesNearestYard()
    {
        var a=new Prefab{Uid=10,Nodes=[new Node{Uid=30}]};var b=new Prefab{Uid=20,Nodes=[new Node{Uid=40}]};
        var company=new CompanyPlace(50,10,"brand","city","Reliable Name");
        var index=new CompanyPlaceIndex([company],[a,b]);
        Assert.Equal(company,index.AtTerminal(30));Assert.Null(index.AtTerminal(40));Assert.Null(index.AtTerminal(99));
        Assert.Null(new CompanyPlaceIndex([company,company with{Uid=51}],[a]).AtTerminal(30));
        Assert.Null(new CompanyPlaceIndex([company with{DisplayName=null}],[a]).AtTerminal(30));
    }
    [Fact]
    public void MissingNativeRoadNameIsUnresolvedAndExplicitNamesPreserveDirections()
    {
        var road=new Road{Uid=10};var prefab=new Prefab{Uid=20};
        var index=new RoadNameIndex([road,prefab],[new(20,RoadNameStatus.Found,"verified-physical-binding","First Road",EntryNodeUid:30),new(20,RoadNameStatus.Found,"verified-physical-binding","Second Road",EntryNodeUid:40)]);
        Assert.Equal(RoadNameStatus.Unresolved,index.Query(10).Status);Assert.Equal(RoadNameStatus.Unresolved,index.Query(99).Status);
        Assert.Equal(2,index.Query(20).Labels.Count);Assert.Equal(RoadNameStatus.Found,index.Query(20).Status);
        Assert.Equal(RoadNameStatus.Unnamed,new RoadNameIndex([road],[new(10,RoadNameStatus.Unnamed,"verified-empty-road-name")]).Query(10).Status);
        Assert.Throws<ArgumentException>(()=>new RoadNameIndex([road],[new(10,RoadNameStatus.Found,"nearby-sign")]));
    }
    [Fact]
    public void NamedCompanyChangesOnlyExecutionWording()
    {
        Assert.Equal("到达Stórmarkaður货场附近，本次导航结束。",NavigationPrompts.Execute(ManeuverKind.Destination,"到达Stórmarkaður货场附近，本次导航结束"));
        Assert.Equal("到达目的地附近，导航结束。",NavigationPrompts.Execute(ManeuverKind.Destination));
        Assert.Equal("两百米后，到达目的地。",NavigationPrompts.AtDistance(ManeuverKind.Destination,200));
    }
    [Fact]
    public void RuntimeStructurePlannerCarriesNamedDestinationToActualExecutionTrigger()
    {
        var now=DateTimeOffset.Parse("2026-11-14T00:00:00Z");
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(0,0,200),default);
        var prefab=new Prefab{Uid=90,Nodes=[road.ForwardNode]};
        var route=RouteSnapshot.Parse([new(road.Node.Uid,3800,60),new(road.ForwardNode.Uid,0,0)],now);
        var nearby=new NearbyMapSnapshot(map,[],[],[]){Companies=new([new(91,90,"brand","city","Stórmarkaður")],[prefab])};
        var runtime=new NavigationRuntime();
        GameDataSnapshot Data(double z)=>new(new(0,0,z,.5,0,380,true,false,1,now),route,RouteAvailability.Available,"s",true);
        Assert.Equal("一百五十米后，到达目的地。",runtime.Process(Data(50),nearby,now).LastSpoken);
        Assert.Equal("到达Stórmarkaður货场附近，本次导航结束。",runtime.Process(Data(180),nearby,now).LastSpoken);
    }
    [Fact]
    public void ShortFinalTurnAlsoCarriesCompanyNameWhenDestinationHasNoIndependentWindow()
    {
        var turn=new NavigationManeuver("turn",ManeuverKind.TurnRight,100,"a","b"){EndDistanceMeters=150,JunctionId="junction"};
        var destination=new NavigationManeuver("destination",ManeuverKind.Destination,150,"b","c")
            {EndDistanceMeters=150,DestinationCompanyName="Stórmarkaður",ExecutionInstruction="到达Stórmarkaður货场附近，本次导航结束"};
        var plan=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,[turn,destination],true,false,false,new());
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.Execution&&t.Text=="右转，然后五十米到达Stórmarkaður货场。");
    }
}
