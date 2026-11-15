using System.Numerics;using LinkEts2Nav.Core;using LinkEts2Nav.Core.Guidance;using LinkEts2Nav.Map;using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class EntranceActionPointIntegrationTests
{
 [Fact]
 public void SelectLanesClampsTheGeneratedEntranceMergeAtThePreviousPhysicalEnd()
 {
  PrefabLanePath Lane(ulong uid,ulong from,ulong to,int input,int output,int inputs,int outputs,int order)=>
   new(uid,from,to,input,output,input,order,inputs,outputs,[input*10+output],
    [new(0,0,0),new(10,0,0)],10){CurveBlinkers=[LaneBlinker.NoBlinker],CurveStartPointIndices=[0]};
  var own=Lane(1,10,20,0,0,1,2,0);var added=Lane(1,11,20,0,1,1,2,1);
  var main=new DirectedRoad("prefab:1:0:1",10,20,own.Points){LanePaths=[own],LaneConnections=[new(10,0,20,0),new(11,0,20,1)],JoinPriority=new(10,[],[],20,"proven")};
  var side=main with{Id="prefab:1:2:1",StartNodeUid=11,LanePaths=[added]};
  var road=new DirectedRoad("road",20,30,[new(10,0,0),new(210,0,0)]);
  var kept=Lane(2,30,40,0,0,2,1,0);var leaving=Lane(2,30,41,1,0,2,1,0);
  var mainExit=new DirectedRoad("prefab:2:0:1",30,40,[new(210,0,0),new(310,0,0)]){LanePaths=[kept]};
  var sideExit=mainExit with{Id="prefab:2:0:2",EndNodeUid=41,LanePaths=[leaving]};
  DirectedRoad[] graph=[main,side,road,mainExit,sideExit];DirectedRoad[] route=[side,road,mainExit];
  var native=new NativeLaneGraph(graph);var auxiliaries=AuxiliaryLaneIndex.Build(native,TestContext.Current.CancellationToken);
  var lanes=new LaneRoutePlanner().Build(route,graph:graph);var view=LaneContractionView.Build(route,auxiliaries);
  var raw=Assert.Single(LaneContractionGuidance.Apply([],view.Select([],lanes)));
  var priorEnd=raw.DistanceMeters+30;
  var preceding=new NavigationManeuver("preceding",ManeuverKind.TurnRight,0,"in","out")
   {JunctionId="previous-real-junction",EndDistanceMeters=priorEnd};
  var scene=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=graph,AuxiliaryLanes=auxiliaries};
  var source=new RoadPathSource(scene.Map,graph,native:native);
  var plan=new NavigationStructurePlan(route,route,[preceding],lanes,[],[],route.Sum(RouteGeometry.Length),false,double.PositiveInfinity)
   {BaseActions=[preceding],Auxiliaries=auxiliaries,Contractions=view};
  var selected=new NavigationStructurePlanner().SelectLanes(plan,lanes,scene,source);
  var final=Assert.Single(selected.Actions,a=>a.Id==raw.Id);
  Assert.Equal(priorEnd,final.DistanceMeters);Assert.Equal(raw.ApproachInterval,final.ApproachInterval);
  Assert.Equal(raw.EndDistanceMeters,final.EndDistanceMeters);Assert.True(final.EndDistanceMeters>final.ApproachInterval!.EndMeters);
  var now=DateTimeOffset.UtcNow;var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
  var speech=new NavigationEventPlanner().Build("s","r",now,0,selected.Actions,true,false,false,scheduler,"f");
  foreach(var along in new[]{raw.DistanceMeters,priorEnd}){
   timeline.Publish(speech with{PlayerAlongMeters=along});
   Assert.DoesNotContain(scheduler.Update(timeline,now,15),t=>t.Disposition==TriggerDisposition.Queued&&t.Event.Id.StartsWith(raw.Id));
  }
  timeline.Publish(speech with{PlayerAlongMeters=priorEnd+.1});
  Assert.Contains(scheduler.Update(timeline,now,15),t=>t.Disposition==TriggerDisposition.Queued&&t.Event.Id.StartsWith(raw.Id));
 }
}
