using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

internal static class GuidanceTestSeries
{
    public static SpeechSequence Route(string id,double end,IReadOnlyList<GuidanceEvent> events,IReadOnlyList<SpeechTrigger> points,bool ended=false)
        =>new(id,new RouteSpeechEnd(end),events,points,ended);

    // Playback tests use the same complete-series scheduler entry as production.
    public static IReadOnlyList<GuidanceTriggerResult> Due(QueuedGuidanceSpeech speech,bool ended=false)
    {
        var now=DateTimeOffset.UtcNow;
        var e=new GuidanceEvent(speech.EventId,100,speech.Text){SpeechSequenceId=speech.SequenceId};
        var point=new SpeechTrigger(speech.TriggerId,speech.EventId,SpeechTriggerKind.Advance,0,speech.Text)
            {SequenceOrder=speech.SequenceOrder,Priority=speech.Priority};
        var timeline=new GuidanceTimeline();
        timeline.Publish(new(speech.Source,"test","r",ended?101:0,true,now,
            [Route(speech.SequenceId,100,[e],ended?[]:[point],ended)]));
        return new SpeechTriggerScheduler().Update(timeline,now,0);
    }
}
