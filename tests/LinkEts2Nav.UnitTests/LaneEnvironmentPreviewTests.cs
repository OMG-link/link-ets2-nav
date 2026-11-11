using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class LaneEnvironmentPreviewTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static NavigationManeuver Lane(double distance,string id="lane")=>new(id,ManeuverKind.MergeLeft,distance,"lane","lane")
        {EndDistanceMeters=distance,AnnouncementMode=NavigationAnnouncementMode.DistancePreview,Instruction="车道合并，建议提前走左侧车道"};
    [Fact]
    public void FiveHundredAndTwoHundredArePreparedAndShareAPlaybackSeriesWithoutExecution()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        var plan=p.Build("s","r",Now,0,[Lane(700)],true,false,false,s);t.Publish(plan);
        Assert.Equal(new[]{"五百米后，车道合并，建议提前走左侧车道。","两百米后，车道合并，建议提前走左侧车道。"},s.Prepare(t,Now));
        Assert.All(plan.Triggers,c=>Assert.Equal(SpeechTriggerKind.Advance,c.Kind));
        Assert.Equal(700,Assert.Single(plan.Sequences).EndAlongMeters);
        t.Publish(p.Build("s","r",Now,200,[Lane(500)],true,false,false,s));
        Assert.StartsWith("五百米后",Assert.Single(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        t.Publish(p.Build("s","r",Now,500,[Lane(200)],true,false,false,s));
        Assert.StartsWith("两百米后",Assert.Single(s.Update(t,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Text);
        t.Publish(p.Build("s","r",Now,701,[],true,false,false,s));
        Assert.Equal(TriggerDisposition.Ended,Assert.Single(s.Update(t,Now,0)).Disposition);
    }
    [Fact]
    public void AFirstLaneWarningAtFourHundredFiftyHasNoLaterTwoHundredStage()
    {
        var plan=new NavigationEventPlanner().Build("s","r",Now,0,[Lane(450)],true,false,false,new());
        Assert.Equal("四百五十米后，车道合并，建议提前走左侧车道。",Assert.Single(plan.Triggers).Text);
    }
    [Fact]
    public void SameKindLaneEventsWaitForTheEarlierMergeAndDoNotChangeTheNavigationBriefing()
    {
        var p=new NavigationEventPlanner();
        NavigationManeuver Turn(string id,double start,double end)=>new(id,ManeuverKind.TurnLeft,start,"a","b"){EndDistanceMeters=end};
        var plan=p.Build("s","r",Now,0,[Turn("first",100,150),Lane(300),Turn("next",600,650),Lane(750,"next-lane")],true,false,false,new());
        Assert.Equal(150,Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("next@")&&t.Kind==SpeechTriggerKind.Overview).AlongMeters);
        var later=Assert.Single(plan.Triggers,t=>t.EventId.StartsWith("next-lane@"));
        Assert.Equal(300,later.AlongMeters);Assert.StartsWith("四百五十米后",later.Text);
    }
    [Fact]
    public void RouteReplacementRemovesTheOldEnvironmentPredecessor()
    {
        var p=new EnvironmentPreviewPlanner();var s=new SpeechTriggerScheduler();
        EnvironmentPreviewEvent E(string id,double x)=>new(id,"camera",x,x,d=>d.ToString());
        p.Build("c",0,[E("old",300)],s,true);
        p.Reconcile(new HashSet<string>{"new"},0,s,"c");
        var plan=p.Build("c",0,[E("new",600)],s,true);
        Assert.Equal(100,plan.Triggers[0].AlongMeters);Assert.Equal("500",plan.Triggers[0].Text);
    }
}
