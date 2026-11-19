using System.Numerics;using LinkEts2Nav.Core;using LinkEts2Nav.Core.Guidance;using LinkEts2Nav.Map;using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class EntranceActionPointIntegrationTests
{
 [Fact]
 public void StaticActionsClampTheGeneratedEntranceMergeAtThePreviousPhysicalEnd()
 {
  // Reuse the native soft-entry/death topology, not legacy geometric transfer inference.
  var graph=ComponentContinuityTests.Soft();
  var terminal=graph[3] with{LanePaths=[..graph[3].LanePaths,..graph[4].LanePaths]};
  var native=new NativeLaneGraph([graph[0],graph[1],graph[2],terminal]);
  DirectedRoad[] route=[graph[1],graph[2],terminal];
  var lanes=new LaneRoutePlanner().Build(route);
  var raw=Assert.Single(new ComponentManeuverPlanner().Build(route,native.Edges,lanes)[0],a=>a.ApproachInterval is not null);
  var priorEnd=raw.DistanceMeters+30;
  var preceding=new NavigationManeuver("preceding",ManeuverKind.TurnRight,0,"in","out")
   {JunctionId="previous-real-junction",EndDistanceMeters=priorEnd};
  var scene=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=native.Edges,NativeGraph=native,Components=ComponentContinuityIndex.Build(native)};
  var source=new RoadPathSource(scene.Map,native.Edges,native:native);
  var staticPlan=new NavigationStructurePlanner().Build(route,new([],"fixture",RouteAvailability.None,DateTimeOffset.UtcNow),scene,source,lanes,Enumerable.Range(0,route.Length).ToArray());
  var selected=staticPlan with{Actions=ActionPointConstraints.Apply([preceding,..staticPlan.Actions])};
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
