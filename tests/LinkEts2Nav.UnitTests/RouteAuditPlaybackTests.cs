using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Probe;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class RouteAuditPlaybackTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly GameDataSnapshot Frame = new(new(0,0,0,0,12,1000,true,false,1,Now),
        RouteSnapshot.Parse([], Now), RouteAvailability.None, "session", true);

    private static SpeechSequence Sequence(string id, string text, double at, int priority, SpeechFollowOn? follow = null)
        => new(id, new RouteSpeechEnd(at + 150), [new(id, at, text)],
            [new(id + ":execute", id, SpeechTriggerKind.Execution, at, text) { Priority = priority, FollowOn = follow }]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordsFinalDispatchAfterTheOtherActionIsCovered(bool helperFirst)
    {
        var offered = new List<string>();
        var played = new List<QueuedGuidanceSpeech>();
        using var audit = new AuditSpeechPlayback(played.Add);
        audit.Guidance.Queued += cue => offered.Add(cue.Text);
        var first = Sequence("first", "turn then straight", 0, helperFirst ? 10 : 0,
            new("helper", "helper", "helper:execute", "turn"));
        var helper = Sequence("helper", "straight", 0, helperFirst ? 0 : 10);
        var future = Sequence("future", "later", 800, 5);
        audit.Guidance.Publish(new("route","session","revision",0,true,Now,[first,helper,future]));
        audit.Guidance.Tick(Frame, Now);
        audit.FinishBatch();

        Assert.Contains(first.Triggers[0].Text, offered);
        Assert.Contains(helper.Triggers[0].Text, offered);
        Assert.Equal(helperFirst ? ["straight", "turn"] : new[] {"turn then straight"}, played.Select(p => p.Text));
        Assert.All(played, p => Assert.Equal("Execution", p.Stage));
        Assert.DoesNotContain(played, p => p.EventId == "future");
    }
}
