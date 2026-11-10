using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class LimitConflictLoggingTests
{
    [Fact]
    public void MergeConflictIsLoggedOnceAndCannotBeHiddenByResourceSummary()
    {
        var warnings=new List<string>();var reporter=new NavigationLogReporter(_=>{},warnings.Add);
        const string conflict="限速汇合冲突：节点=123，限速=50/60";
        var scene=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],["a","b","c",conflict],[]);
        var now=DateTimeOffset.UtcNow;
        reporter.Scanned(scene,Vector3.Zero,TimeSpan.Zero,now);
        reporter.Scanned(scene,Vector3.Zero,TimeSpan.Zero,now.AddMinutes(1));
        Assert.Single(warnings,w=>w.Contains(conflict));
    }
}
