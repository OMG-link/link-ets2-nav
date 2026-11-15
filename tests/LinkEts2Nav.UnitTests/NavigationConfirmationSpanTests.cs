using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class NavigationConfirmationSpanTests
{
    private static (NavigationStructurePlan Plan,NavigationManeuverDetector Detector) Scene()
    {
        DirectedRoad Road(string id,ulong a,ulong b,float from,float to)=>new(id,a,b,[new(0,0,from),new(0,0,to)]);
        var first=Road("prefab:A:0:1",2,3,100,140);var second=Road("prefab:B:0:1",4,5,300,340);
        DirectedRoad[] route=[Road("before",1,2,0,100),first,Road("between",3,4,140,300),second,Road("after",5,6,340,1000)];
        DirectedRoad[] graph=[..route,Road("prefab:A:0:2",2,99,100,140),Road("prefab:B:0:2",4,98,300,340)];
        var detector=new NavigationManeuverDetector();
        var actions=detector.FindSequence(route,0,graph,out double end,out bool unknown,out double firstUnknown);
        var plan=new NavigationStructurePlan(route,route,actions,new([],[],true,null),[],[],end,unknown,firstUnknown)
            {UnresolvedSpans=detector.UnresolvedSpans.ToArray()};
        return(plan,detector);
    }

    [Fact]
    public void DetectorRetainsAllUncertainComponentsAndProgressOnlyQueriesUnpassedIntervals()
    {
        var (plan,_)=Scene();Assert.True(plan.Unresolved);
        Assert.Equal(new[]{new NavigationUnresolvedSpan(100,140),new(300,340)},plan.UnresolvedSpans);
        Assert.Equal(100,plan.ConfirmationEndAfter(50));
        Assert.Equal(100,plan.ConfirmationEndAfter(120)); // Still inside the first unknown component.
        Assert.Equal(300,plan.ConfirmationEndAfter(140));
        Assert.Equal(300,plan.ConfirmationEndAfter(320));
        Assert.Equal(1000,plan.ConfirmationEndAfter(340));
    }

    [Fact]
    public void ARealActionAfterEarlierUnknownsCanPreplanItsLaterContinuation()
    {
        var (structure,_)=Scene();
        var action=new NavigationManeuver("merge",ManeuverKind.MergeLeft,380,"in","out"){EndDistanceMeters=430};
        double boundary=structure.ConfirmationEndAfter(action.EndDistanceMeters!.Value);
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var now=DateTimeOffset.UtcNow;
        var plan=planner.Build("session","route",now,0,[action],true,false,true,scheduler,"frame",confirmedContinuationEndAlong:boundary);
        var continuation=Assert.Single(plan.Sequences,s=>s.Id.StartsWith("continuation:"));
        Assert.Equal(430,Assert.Single(continuation.Triggers).AlongMeters);Assert.Equal(1000,continuation.EndAlongMeters);
        timeline.Publish(plan);scheduler.Update(timeline,now,10);
        timeline.UpdateProgress("navigation","session",431,now.AddSeconds(1),true);
        Assert.Contains(scheduler.Update(timeline,now.AddSeconds(1),10),r=>r.Trigger.Text==NavigationPrompts.Continue()&&r.Disposition==TriggerDisposition.Queued);
    }
}
