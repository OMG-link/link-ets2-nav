using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class FerryRouteIndexTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static (TruckLib.ScsMap.Map Map,Road Before,Road After,Ferry A,Ferry B) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var before=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var after=Road.Add(map,new(10000,0,0),new(10200,0,0),default);
        var a=Ferry.Add(map,new(140,0,0),"a");var b=Ferry.Add(map,new(9960,0,0),"b");
        var pa=new Prefab{Nodes=[before.ForwardNode],Ferry=a};var pb=new Prefab{Nodes=[after.Node],Ferry=b};
        map.MapItems[pa.Uid]=pa;map.MapItems[pb.Uid]=pb;a.Prefab=pa;b.Prefab=pb;
        return(map,before,after,a,b);
    }
    private static RouteSnapshot Route(params ulong[] nodes)=>RouteSnapshot.Parse(nodes.Select((uid,i)=>new RouteNode(uid,(nodes.Length-i-1)*100,10)),Now);

    [Theory][InlineData(false)][InlineData(true)]
    public void OnlyOrderedActivationNodesAndALegalConnectionSelectThePortDirection(bool reverse)
    {
        var s=Scene();var ids=new[]{s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.A.Node.Uid,s.B.Node.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid};
        if(reverse)Array.Reverse(ids);
        var index=new FerryJourneyIndex(s.Map,[("a","b")],new Dictionary<string,string>{{"a","Port A"},{"b","Port B"}});
        var leg=Assert.Single(index.OnRoute(Route(ids)));
        Assert.Equal(reverse?"b":"a",leg.Source.Id);Assert.Equal(reverse?"a":"b",leg.Target.Id);
        Assert.Equal(reverse?s.After.Node.Uid:s.Before.ForwardNode.Uid,leg.EntranceUid);
        Assert.Equal(reverse?"Port B":"Port A",leg.Source.Name);
        Assert.Empty(index.OnRoute(Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.A.Node.Uid,999,s.B.Node.Uid,s.After.Node.Uid)));
        Assert.Empty(new FerryJourneyIndex(s.Map,[]).OnRoute(Route(ids)));
    }

    [Fact]
    public void RailConnectionsRemainValidForJourneyCostButNeverBecomeFerryGuidance()
    {
        var s=Scene();s.A.TrainTransport=true;
        var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        Assert.True(index.Connects(s.A.Node.Uid,s.B.Node.Uid));
        Assert.Empty(index.OnRoute(Route(s.Before.ForwardNode.Uid,s.A.Node.Uid,s.B.Node.Uid,s.After.Node.Uid)));
    }
}
