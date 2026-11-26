using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class RoundaboutSeriesTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static NavigationManeuver[] Visit(double player,double entry=500,double end=800,string selected="third")=>[
        new("ring:"+selected+":enter",ManeuverKind.EnterRoundabout,entry-player,"in","out")
            {JunctionId="ring",SpeechSequenceId="ring:in",SpeechSequenceStartDistanceMeters=entry-player,EndDistanceMeters=end-player},
        new("ring:"+selected+":one",ManeuverKind.PassRoundaboutExit,entry+80-player,"in","out")
            {JunctionId="ring",SpeechSequenceId="ring:in",SpeechSequenceStartDistanceMeters=entry-player,EndDistanceMeters=end-player,
                ExecutionOnly=true,SpeechSequenceOrder=1,Instruction="经过第一出口"},
        new("ring:"+selected+":out",ManeuverKind.ExitRoundabout,end-30-player,"in","out")
            {JunctionId="ring",SpeechSequenceId="ring:in",SpeechSequenceStartDistanceMeters=entry-player,EndDistanceMeters=end-player,
                ExecutionOnly=true,SpeechSequenceOrder=3,Instruction="驶出环岛"}];
    [Fact]
    public void DetectorUsesOneSeriesFromEntranceThroughAllDepartures()
    {
        var road=new DirectedRoad("ring",10,20,[Vector3.Zero,new(200,0,0)]);
        var range=RoundaboutNavigationTests.FixtureRange(road,[50,100,150]);
        var actions=RoundaboutNavigationTests.Detect([road],[range]);
        Assert.Single(actions.Select(m=>m.SpeechSequenceId).Distinct());
        Assert.All(actions,m=>Assert.Equal(0,m.SpeechSequenceStartDistanceMeters));
        Assert.Equal(new double[]{0,1,2,3},actions.Select(m=>m.SpeechSequenceOrder));
    }
    [Fact]
    public void EntryAndDeparturesReplaceEarlierPendingPointsAndEndTogether()
    {
        var p=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var plan=p.Build("s","r",Now,0,Visit(0),true,false,false,scheduler);
        Assert.Single(plan.Sequences);Assert.Single(plan.Events.Select(e=>e.SequenceId).Distinct());
        timeline.Publish(plan);scheduler.Update(timeline,Now,0);
        timeline.Publish(p.Build("s","r",Now,780,Visit(780).Where(m=>m.DistanceMeters>=0).ToArray(),true,false,false,scheduler));
        var result=scheduler.Update(timeline,Now,0);
        Assert.Equal("驶出环岛。",Assert.Single(result,r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        Assert.Contains(result,r=>r.Trigger.Kind==SpeechTriggerKind.Execution&&r.Trigger.SequenceOrder==0&&r.Disposition==TriggerDisposition.Superseded);
        timeline.Publish(p.Build("s","r",Now,801,[],true,false,false,scheduler));
        Assert.Single(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Ended);
    }
    [Fact]
    public void RoutePrefixAndExitChangesReuseEntranceAnchorAndUpdateWholeVisitEnd()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();
        var first=p.Build("s","old",Now,0,Visit(0),true,false,false,s,coordinateFrame:"f");
        string id=Assert.Single(first.Sequences).Id;
        var changed=Visit(550,500.00000001,900,"fourth").Where(m=>m.DistanceMeters>=0).ToArray();
        var next=p.Build("s","new",Now,550,changed,true,false,false,s,coordinateFrame:"f",replaceRoute:true);
        Assert.Equal(id,Assert.Single(next.Sequences).Id);
        Assert.All(next.Sequences,q=>Assert.Equal(900,q.EndAlongMeters));
        Assert.Contains(next.Events,e=>e.Id.Contains(":enter"));
        var later=p.Build("s","new",Now,910,[],true,false,false,s,coordinateFrame:"f");
        Assert.Equal(900,Assert.Single(later.Sequences).EndAlongMeters);
    }
    [Fact]
    public void TwoFutureVisitsWithIdenticalModelAndPortsAreDifferentSeries()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();
        var plan=p.Build("s","r",Now,0,[..Visit(0),..Visit(0,1500,1800)],true,false,false,s);
        Assert.Equal(2,plan.Sequences.Count);Assert.Equal(6,plan.Events.Count);
        Assert.Equal(6,plan.Events.Select(e=>e.Id).Distinct().Count());
    }

    [Fact]
    public void ChangingAnUpstreamRingLaneReanchorsDownstreamCuesWithinTheirSdkVisit()
    {
        static NavigationManeuver[] OnRoute(double player,double entry,double end,int visit)=>Visit(player,entry,end)
            .Select(m=>m with{Id=m.Id+":visit:"+visit,SpeechSequenceId="ring:in:visit:"+visit,HasSdkVisitIdentity=true}).ToArray();
        var planner=new NavigationEventPlanner();var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        var first=planner.Build("s","r",Now,0,OnRoute(0,120,270,21),true,false,false,scheduler,sdkRouteGeneration:1);
        timeline.Publish(first);scheduler.Update(timeline,Now,0);
        var original=Assert.Single(first.Sequences);
        var overview=Assert.Single(original.Triggers,t=>t.Kind==SpeechTriggerKind.Overview);
        Assert.True(scheduler.IsConsumed("navigation",overview.Id));

        var next=planner.Build("s","r",Now,25,OnRoute(25,145,295,21),true,false,false,scheduler,sdkRouteGeneration:1);
        var moved=Assert.Single(next.Sequences);
        Assert.Equal(original.Id,moved.Id);
        Assert.Equal(original.Events.Select(e=>e.Id),moved.Events.Select(e=>e.Id));
        Assert.Equal(new[]{145d,225d,265d},moved.Events.Select(e=>e.AlongMeters));
        Assert.Equal(295,moved.EndAlongMeters);
        Assert.True(scheduler.IsConsumed("navigation",Assert.Single(moved.Triggers,t=>t.Kind==SpeechTriggerKind.Overview).Id));

        var replacement=planner.Build("s","new",Now,0,OnRoute(0,145,295,21),true,false,false,scheduler,sdkRouteGeneration:2);
        var nextTrip=Assert.Single(replacement.Sequences);
        Assert.NotEqual(original.Id,nextTrip.Id);Assert.Contains(original.Id,replacement.ReplacedSequenceIds);
        Assert.False(scheduler.IsConsumed("navigation",Assert.Single(nextTrip.Triggers,t=>t.Kind==SpeechTriggerKind.Overview).Id));
    }
}
