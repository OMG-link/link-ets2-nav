using System.Numerics;
using System.Text.Json;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class CurrentSpeedLimitMonitorTests
{
    private static readonly DateTimeOffset Now=new(2026,11,9,0,0,0,TimeSpan.Zero);
    private static GameDataSnapshot Data(DateTimeOffset time,double x=500) => new(
        new(x,0,0,.75,20,1000,true,false,1,time){CurrentSpeedLimitKph=80},
        RouteSnapshot.Parse([],time),RouteAvailability.None,"session",true);
    private static (TruckLib.ScsMap.Map,SpeedLimitResolver) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var road=Road.Add(map,new(0,0,0),new(1000,0,0),default);
        var geometry=new DirectedRoad(road.Uid.ToString("X16"),road.Node.Uid,road.ForwardNode.Uid,
            Enumerable.Range(0,101).Select(i=>new Vector3(i*10,0,0)).ToArray());
        var data=new SpeedLimitData(new Dictionary<ulong,LimitRoad>{{road.Uid,new(geometry,"local_road")}},[],[],
            new Dictionary<string,TruckSpeedLimit>{{"test/local_road",new(60,50,60)}},"test");
        return(map,new(data,new RoadPathSource(map)));
    }
    [Fact]
    public void ComparesSamePoseAndThrottlesThenResamples()
    {
        var(map,resolver)=Scene();var monitor=new CurrentSpeedLimitMonitor();
        var first=monitor.Sample(Data(Now),map,resolver,Now)!;
        Assert.True(first.Mismatch);Assert.Equal(60,first.InferredKph);Assert.Equal(80,first.ActualKph);
        Assert.Same(first,monitor.Sample(Data(Now.AddMilliseconds(200),510),map,resolver,Now.AddMilliseconds(200)));
        var next=monitor.Sample(Data(Now.AddSeconds(1),520),map,resolver,Now.AddSeconds(1))!;
        Assert.Equal(520,next.X);Assert.Equal(Now.AddSeconds(1),next.SampledAt);
        monitor.Reset();Assert.NotSame(next,monitor.Sample(Data(Now.AddSeconds(1),530),map,resolver,Now.AddSeconds(1)));
    }
    [Fact]
    public void PausedAndStaleSamplesDoNotProduceComparisons()
    {
        var(map,resolver)=Scene();var monitor=new CurrentSpeedLimitMonitor();var data=Data(Now);
        Assert.Null(monitor.Sample(data with {Vehicle=data.Vehicle! with {Paused=true}},map,resolver,Now));
        Assert.Null(monitor.Sample(data,map,resolver,Now.AddSeconds(3)));
    }
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
