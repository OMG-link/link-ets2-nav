using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class SpeechSeriesContractTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static readonly GuidanceEvent Event=new("turn",100,"turn");
    private static SpeechTrigger Point(double along)=>new("cue","turn",SpeechTriggerKind.Execution,along,"turn");
    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)]
    public void NonfiniteEndCannotConstructAPlayableSeries(double end)
        =>Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new RouteSpeechEnd(end),[Event],[Point(80)]));
    [Fact]
    public void MissingEndUnboundPointsAndPointsAfterTheEndAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",null!,[Event],[Point(80)]));
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new RouteSpeechEnd(100),[Event],[Point(101)]));
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("other",new RouteSpeechEnd(150),[Event],[Point(80)]));
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new RouteSpeechEnd(150),[Event],[Point(80) with{EventId="missing"}]));
    }
    [Fact]
    public void SpatialEndIsRealAndEndedSeriesCannotIntroduceANewPoint()
    {
        var spatial=new CameraSpeechEnd(new(100,0,0),Vector3.UnitX);
        Assert.Null(new SpeechSequence("turn",spatial,[Event],[Point(80)]).EndAlongMeters);
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",spatial,[Event],[Point(80)],ended:true));
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new CameraSpeechEnd(new(float.NaN,0,0)),[Event],[]));
    }
    [Fact]
    public void LaterPointReplacesEarlierPointAndDuplicateOrReversedRanksAreRejected()
    {
        var first=Point(10) with{Id="early",SequenceOrder=-200};
        var later=Point(80) with{Id="late",SequenceOrder=0};
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new RouteSpeechEnd(150),[Event],[first,later with{SequenceOrder=-200}]));
        Assert.Throws<ArgumentException>(()=>new SpeechSequence("turn",new RouteSpeechEnd(150),[Event],[first,later with{SequenceOrder=-500}]));
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        timeline.Publish(new("navigation","s","r",90,true,Now,[new("turn",new RouteSpeechEnd(150),[Event],[first,later])]));
        Assert.Equal("late",Assert.Single(scheduler.Update(timeline,Now,0),r=>r.Disposition==TriggerDisposition.Queued).Trigger.Id);
    }
    [Fact]
    public void EndedSeriesCannotResurrectAPreviouslyDisabledPoint()
    {
        var timeline=new GuidanceTimeline();var scheduler=new SpeechTriggerScheduler();
        GuidanceSourcePlan Plan(double player,bool enabled)=>new("navigation","s","r",player,true,Now,
            [new("turn",new RouteSpeechEnd(200),[Event],[Point(100) with{Enabled=enabled}])]);
        timeline.Publish(Plan(201,false));
        Assert.Equal(TriggerDisposition.Ended,Assert.Single(scheduler.Update(timeline,Now,0)).Disposition);
        timeline.Publish(Plan(202,true));
        Assert.Empty(scheduler.Update(timeline,Now,0));Assert.Empty(scheduler.Prepare(timeline,Now));
    }
    [Fact]
    public void ConstructionTakesReadOnlySnapshotsRatherThanFollowingMutableInputLists()
    {
        var events=new List<GuidanceEvent>{Event};var points=new List<SpeechTrigger>{Point(80)};
        var series=new SpeechSequence("turn",new RouteSpeechEnd(150),events,points);
        events.Clear();points.Clear();Assert.Single(series.Events);Assert.Single(series.Triggers);
        var input=new List<SpeechSequence>{series};var plan=new GuidanceSourcePlan("navigation","s","r",0,true,Now,input);
        input.Clear();Assert.Single(plan.Sequences);
        var next=new List<SpeechSequence>{series};plan=plan with{Sequences=next};next.Clear();Assert.Single(plan.Sequences);
    }
    [Fact]
    public void ContinuationRequiresTheActualConfirmedCorridorEnd()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();
        Assert.Empty(p.Build("s","r",Now,40,[],true,false,true,s).Sequences);
        var plan=p.Build("s","r",Now,40,[],true,false,true,s,confirmedContinuationEndAlong:2040);
        var series=Assert.Single(plan.Sequences);Assert.Equal(2040,series.EndAlongMeters);
        Assert.Equal(40,Assert.Single(series.Triggers).AlongMeters);
    }
    [Fact]
    public async Task NoNavigationCameraUsesItsPhysicalEndAndPassageClearsPendingSpeech()
    {
        await using var manager=new GuidancePlaybackManager(null,new NoPlayer(),()=>null);
        var c=new GuidanceCoordinator(playback:manager);
        var snapshot=new WarningSnapshot("s","r",false,true,Now,[]);
        PlannedCameraWarning[] points=[new("camera",500,450,WarningDistanceKind.Euclidean,0)];
        var camera=new CameraObservation("camera",new(450,0,0),new(450,0,0),Vector3.UnitX,true,"test","test");
        var data=new GameDataSnapshot(new(0,0,0,0,10,1000,true,false,1,Now),RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true);
        c.PublishLandmarks(snapshot,points);c.Tick(data,Now);Assert.Empty(manager.Pending);
        c.PublishLandmarks(snapshot,points,observations:[camera]);c.Tick(data,Now);Assert.Single(manager.Pending);
        var end=Assert.IsType<CameraSpeechEnd>(Assert.Single(c.Timeline.Sources[0].Sequences).End);
        Assert.Equal(camera.MonitoredPosition,end.Position);Assert.Equal(camera.TravelDirection,end.TravelDirection);
        c.PublishLandmarks(snapshot,[],[new("camera",CameraWarningKind.Calibrated,0)],observations:[camera]);c.Tick(data,Now);
        Assert.Empty(manager.Pending);
    }
    private sealed class NoPlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>throw new InvalidOperationException("Audio is deliberately unavailable.");}
    [Fact]
    public async Task RejectedPublicationReleasesTheAtomicPlaybackHold()
    {
        var player=new ReadyPlayer();bool ready=false;
        await using var manager=new GuidancePlaybackManager(null,player,()=>ready?"ready.wav":null);
        var c=new GuidanceCoordinator(playback:manager);
        var series=new SpeechSequence("turn",new RouteSpeechEnd(150),[Event],[Point(0)]);
        var valid=new GuidanceSourcePlan("navigation","s","r",0,true,Now,[series]);
        var data=new GameDataSnapshot(new(0,0,0,0,10,1000,true,false,1,Now),RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true);
        c.Publish(valid);c.Tick(data,Now);Assert.Single(manager.Pending);
        Assert.Throws<ArgumentException>(()=>c.Publish(valid with{Sequences=[series,series]}));
        ready=true;manager.SetActive(true);
        await player.Started.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
    }
    private sealed class ReadyPlayer:IGuidanceAudioPlayer
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PlayAsync(string file,CancellationToken token){Started.TrySetResult();return Task.CompletedTask;}
    }
}
