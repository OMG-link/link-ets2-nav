using System.Text.Json;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;

public sealed class LimitComparisonArchiveTests
{
    private static readonly DateTimeOffset Now=new(2026,11,9,0,0,0,TimeSpan.Zero);
    private static GameDataSnapshot Data(DateTimeOffset time,double x=500) => new(
        new(x,0,0,.75,20,1000,true,false,1,time){CurrentSpeedLimitKph=80},
        RouteSnapshot.Parse([],time),RouteAvailability.None,"session",true);
    [Fact]
    public void LogsMismatchFailureDistanceReminderAndRecoveryWithoutTickSpam()
    {
        var warnings=new List<string>();var info=new List<string>();var reporter=new NavigationLogReporter(info.Add,warnings.Add);
        var sample=new CurrentSpeedLimitDiagnostic("s",Now,1,2,3,.75,80,60,"country/test");
        reporter.ReportLimitComparison(sample);
        reporter.ReportLimitComparison(sample with {SampledAt=Now.AddSeconds(1),X=10});
        Assert.Single(warnings);
        reporter.ReportLimitComparison(sample with {SampledAt=Now.AddSeconds(2),X=101});
        Assert.Equal(2,warnings.Count);
        reporter.ReportLimitComparison(sample with {SampledAt=Now.AddSeconds(3),InferredKph=null,Reason="missing lane"});
        Assert.Contains("推断失败",warnings.Last());Assert.Contains("实际=80.0",warnings.Last());
        reporter.ReportLimitComparison(sample with {SampledAt=Now.AddSeconds(4),InferredKph=80});
        Assert.Contains("核验恢复",Assert.Single(info));
        reporter.ReportLimitComparison(sample with {SampledAt=Now.AddSeconds(5),InferredKph=80});
        Assert.Single(info);
    }
    [Fact]
    public async Task ReplayKeepsComparisonPoseTimestampAndReason()
    {
        string root=Path.Combine(Path.GetTempPath(),"limit-trace-"+Guid.NewGuid().ToString("N"));
        try
        {
            var sample=new CurrentSpeedLimitDiagnostic("s",Now,1,2,3,.75,80,null,"missing lane");
            await using(var recorder=new NavigationTraceRecorder(root))
                recorder.Record(Data(Now.AddSeconds(1)),new(new(),[],[],[]),
                    new(Now,"running","None",0,[]){CurrentSpeedLimit=sample},"resource","game");
            using var frame=JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path.Combine(root,"motion.jsonl"))));
            var comparison=frame.RootElement.GetProperty("CurrentSpeedLimit");
            Assert.Equal(Now,comparison.GetProperty("SampledAt").GetDateTimeOffset());
            Assert.Equal(1,comparison.GetProperty("X").GetDouble());
            Assert.Equal("missing lane",comparison.GetProperty("Reason").GetString());
            Assert.Equal(JsonValueKind.Null,comparison.GetProperty("InferredKph").ValueKind);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
