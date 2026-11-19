using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class LoadedConnectionDiagnosticTests
{
    private static RouteSnapshot Route(params ulong[] ids)=>RouteSnapshot.Parse(
        ids.Select((uid,i)=>new RouteNode(uid,(ids.Length-i)*100,0)),DateTimeOffset.UtcNow);
    private static (TruckLib.ScsMap.Map Map,Road First,Road Second) Roads()
    {
        var map=new TruckLib.ScsMap.Map();
        return(map,Road.Add(map,Vector3.Zero,new(100,0,0),default),
            Road.Add(map,new(200,0,0),new(300,0,0),default));
    }
    [Fact]
    public void LoadedSdkGapReportsItsOriginalPairAndBothReasonsOnlyOnce()
    {
        var s=Roads();var log=new List<string>();
        var source=new RoadPathSource(s.Map,log:log.Add,missingConnection:uid=>
            new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,$"native {uid:X16}: NavCurves=0"));
        var ids=new[]{s.First.Node.Uid,s.First.ForwardNode.Uid,s.Second.Node.Uid,s.Second.ForwardNode.Uid};
        Assert.Equal(2,source.GetRouteComponents(Route(ids)).Count);
        source.GetRouteComponents(Route(ids.Skip(1).ToArray()));
        var failure=Assert.Single(log.Where(x=>x.Contains("导航连接解析错误")));
        Assert.Contains($"{ids[1]:X16} → {ids[2]:X16}",failure);
        Assert.Contains($"native {ids[1]:X16}: NavCurves=0",failure);
        Assert.Contains($"native {ids[2]:X16}: NavCurves=0",failure);
    }
    [Fact]
    public void GloballyKnownNodesWithMissingOwnerStillRequestMapWithoutError()
    {
        var s=Roads();var log=new List<string>();var requests=new List<MapQueryIssue>();
        var source=new RoadPathSource(s.Map,log:log.Add,missingConnection:_=>new MissingMapObjects([new(MapObjectKind.Item,83)],"owner not loaded"),reportMapIssue:requests.Add);
        source.GetRouteComponents(Route(s.First.ForwardNode.Uid,s.Second.Node.Uid));
        Assert.Empty(log);Assert.NotEmpty(requests);Assert.All(requests,x=>Assert.IsType<MissingMapObjects>(x));
    }
    [Fact]
    public void OrdinaryRouteEndDoesNotInventAnUnresolvedContinuation()
    {
        var s=Roads();var log=new List<string>();
        var source=new RoadPathSource(s.Map,log:log.Add);
        Assert.Single(source.GetRouteComponents(Route(s.First.Node.Uid,s.First.ForwardNode.Uid)));
        Assert.Empty(log);Assert.Empty(source.MapIssues);
    }
    [Fact]
    public void VerifiedNativeSpanDoesNotReportTheIntermediateUnmappedSdkNode()
    {
        var map=new TruckLib.ScsMap.Map();var log=new List<string>();
        var a=Road.Add(map,new(-100,0,0),Vector3.Zero,default);
        var b=Road.Add(map,new(100,0,0),new(200,0,0),default);
        var span=new DirectedRoad("prefab:42:0:1",a.ForwardNode.Uid,b.Node.Uid,[Vector3.Zero,new(100,0,0)]);
        var source=new RoadPathSource(map,[span],log:log.Add);
        Assert.Single(Assert.Single(source.GetRouteComponents(Route(a.ForwardNode.Uid,999,b.Node.Uid))));
        Assert.Empty(log);Assert.Empty(source.MapIssues);
    }
}
