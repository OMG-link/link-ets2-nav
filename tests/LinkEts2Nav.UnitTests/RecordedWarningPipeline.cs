using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

/// <summary>Runs the same observe/publish/tick cycle as the host, without an audio player.</summary>
internal sealed class RecordedWarningPipeline
{
    public GuidanceCoordinator Guidance {get;}=new();
    public List<QueuedGuidanceSpeech> Queued {get;}=[];
    public NavDiagnosticSnapshot? Latest {get;private set;}
    private readonly WarningPipeline pipeline;
    public RecordedWarningPipeline()
    {
        Guidance.Queued+=Queued.Add;
        pipeline=new(guidance:Guidance);
    }
    public NavDiagnosticSnapshot Process(GameDataSnapshot data,NearbyMapSnapshot nearby,DateTimeOffset now)
    {
        Guidance.Observe(data,now);Latest=pipeline.Process(data,nearby,now);Guidance.Tick(data,now);
        return Latest;
    }
}
