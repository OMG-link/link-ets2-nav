using System.Threading.Channels;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class ContinuationReplacementTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-14T09:43:53Z");
    private static NavigationManeuver Turn(double distance)=>new("next",ManeuverKind.TurnLeft,distance,"in","out"){EndDistanceMeters=distance+40};
    private static GameDataSnapshot Data()=>new(new(0,0,0,0,10,10000,true,false,1,Now),RouteSnapshot.Parse([],Now),RouteAvailability.None,"s",true);
    private static NavigationManeuver[] Ring()=>[
        new("ring:enter",ManeuverKind.EnterRoundabout,300,"ring","ring")
            {EndDistanceMeters=400,SpeechSequenceId="ring:physical",SpeechSequenceStartDistanceMeters=300,JunctionId="ring"},
        new("ring:exit",ManeuverKind.ExitRoundabout,380,"ring","ring")
            {EndDistanceMeters=400,SpeechSequenceId="ring:physical",SpeechSequenceStartDistanceMeters=300,JunctionId="ring",ExecutionOnly=true,SpeechSequenceOrder=1}];

    [Fact]
    public void AResolvedFarActionReplacesTheUnknownTailBeforeBothBecomeDue()
    {
        var p=new NavigationEventPlanner();var s=new SpeechTriggerScheduler();var t=new GuidanceTimeline();
        var unknown=p.Build("s","r",Now,0,Ring(),true,false,true,s,"frame",confirmedContinuationEndAlong:6400);
        string old=Assert.Single(unknown.Sequences,x=>x.Id.StartsWith("continuation:")).Id;
        Assert.Contains("ring:physical",old);Assert.DoesNotContain("ring:exit",old);
        t.Publish(unknown);s.Update(t,Now,10);
        var known=p.Build("s","r",Now,0,[..Ring(),Turn(9800)],true,false,false,s,"frame");
        Assert.Contains(old,known.ReplacedSequenceIds);t.Publish(known);
        t.UpdateProgress("navigation","s",401,Now,true);
        var due=s.Update(t,Now,10).Where(r=>r.Disposition==TriggerDisposition.Queued).ToArray();
        Assert.Equal("沿当前道路，继续行驶九点四公里。",Assert.Single(due).Trigger.Text);
        Assert.DoesNotContain(t.Sources[0].Sequences,x=>x.Id==old);
        // A later smaller map window must not resurrect the earlier wording.
        var regressed=p.Build("s","r",Now,500,[],true,false,true,s,"frame",confirmedContinuationEndAlong:1000);
        t.Publish(regressed);Assert.DoesNotContain(t.Sources[0].Sequences,x=>x.Id==old);
        Assert.DoesNotContain(s.Update(t,Now,10),r=>r.Trigger.Text==NavigationPrompts.Continue());
    }

    [Theory]
    [InlineData(500,"五百米后，左转。")]
    [InlineData(9400,"沿当前道路，继续行驶九点四公里。")]
    public async Task AQueuedUnknownPreviewIsWithdrawnBeforeTheResolvedDistanceQueues(double distance,string text)
    {
        await using var playback=new GuidancePlaybackManager(null,new ImmediatePlayer(),()=>null,cancellationToken:TestContext.Current.CancellationToken);
        var coordinator=new GuidanceCoordinator(playback:playback);var p=new NavigationEventPlanner();
        coordinator.Publish(p.Build("s","r",Now,0,[],true,false,true,coordinator.Scheduler,"f",confirmedContinuationEndAlong:1000));
        coordinator.Tick(Data(),Now);Assert.Equal(NavigationPrompts.Continue(),Assert.Single(playback.Pending).Text);
        coordinator.Publish(p.Build("s","r",Now,0,[Turn(distance)],true,false,false,coordinator.Scheduler,"f"));
        Assert.Empty(playback.Pending);coordinator.Tick(Data(),Now);
        Assert.Equal(text,Assert.Single(playback.Pending).Text);
        Assert.DoesNotContain(coordinator.Timeline.Sources[0].Triggers,t=>t.Text==NavigationPrompts.Continue());
    }

    [Fact]
    public async Task AnAlreadyPlayedUnknownPreviewAllowsOneDistanceSupplementWithoutLosingNearStages()
    {
        var played=Channel.CreateUnbounded<QueuedGuidanceSpeech>();
        await using var playback=new GuidancePlaybackManager(null,new ImmediatePlayer(),()=>"mock.wav",cancellationToken:TestContext.Current.CancellationToken);
        playback.Played+=item=>played.Writer.TryWrite(item);
        var coordinator=new GuidanceCoordinator(playback:playback);var p=new NavigationEventPlanner();
        coordinator.Publish(p.Build("s","r",Now,0,[],true,false,true,coordinator.Scheduler,"f",confirmedContinuationEndAlong:1000));
        coordinator.Tick(Data(),Now);
        var unknown=await played.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.Equal(NavigationPrompts.Continue(),unknown.Text);
        var known=p.Build("s","r",Now,0,[Turn(9400)],true,false,false,coordinator.Scheduler,"f");
        coordinator.Publish(known);coordinator.Tick(Data(),Now);
        var distance=await played.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.Equal("沿当前道路，继续行驶九点四公里。",distance.Text);
        coordinator.Publish(known);coordinator.Tick(Data(),Now);Assert.False(played.Reader.TryRead(out _));
        Assert.Contains(known.Triggers,t=>t.Id.EndsWith(":2000"));
        Assert.Contains(known.Triggers,t=>t.Kind==SpeechTriggerKind.Execution);
    }

    private sealed class ImmediatePlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>Task.CompletedTask;}
}
