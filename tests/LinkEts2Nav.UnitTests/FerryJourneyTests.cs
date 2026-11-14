using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class FerryJourneyTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    private static RouteSnapshot Route(params ulong[] ids)=>RouteSnapshot.Parse(ids.Select((id,i)=>new RouteNode(id,(ids.Length-i-1)*100000,99999)),Now);
    private static (TruckLib.ScsMap.Map Map,Road Before,Road After,Ferry A,Ferry B) Scene()
    {
        var map=new TruckLib.ScsMap.Map();
        var before=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var after=Road.Add(map,new(100000,0,0),new(100200,0,0),default);
        var a=Ferry.Add(map,new(101,0,0),"a");var b=Ferry.Add(map,new(99999,0,0),"b");
        var pa=new Prefab{Nodes=[before.ForwardNode],Ferry=a};var pb=new Prefab{Nodes=[after.Node],Ferry=b};
        map.MapItems[pa.Uid]=pa;map.MapItems[pb.Uid]=pb;
        before.ForwardNode.ForwardItem=pa;after.Node.BackwardItem=pb;
        a.Prefab=pa;b.Prefab=pb;
        return(map,before,after,a,b);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void CrossingAddsExactlyOneMinuteAndNoSeaDistanceInEitherDirection(bool reverse)
    {
        var s=Scene();var ids=new[]{s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid};
        if(reverse)Array.Reverse(ids);
        var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1.028,index).Estimate(Route(ids));
        Assert.True(result.Ready,result.Failure);Assert.Equal(300,result.WorldMeters!.Value,2);
        Assert.Equal(300/(80*.8/3.6)+60,result.RealSeconds!.Value,2);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ActivationNodesAndFerryItemUidsBothIdentifyExplicitPortLinks(bool itemUid)
    {
        var s=Scene();var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>throw new Exception("No road speed for ferry"),1,index)
            .Estimate(Route(itemUid?s.A.Uid:s.A.Node.Uid,itemUid?s.B.Uid:s.B.Node.Uid));
        Assert.True(result.Ready,result.Failure);Assert.Equal(0,result.WorldMeters);Assert.Equal(60,result.RealSeconds);
    }
    [Fact]
    public void RepeatedCrossingsAreChargedOncePerOccurrenceAndEqualSdkDistancesAreAllowed()
    {
        var s=Scene();var route=RouteSnapshot.Parse(new[]{s.A.Node.Uid,s.B.Node.Uid,s.A.Node.Uid,s.B.Node.Uid}
            .Select(id=>new RouteNode(id,0,0)),Now);
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1,new(s.Map,[("a","b")])).Estimate(route);
        Assert.True(result.Ready,result.Failure);Assert.Equal(0,result.WorldMeters);Assert.Equal(180,result.RealSeconds);
    }
    [Fact]
    public void UnconnectedPortsNeverBecomeAGuessedTransportLink()
    {
        var s=Scene();var index=new FerryJourneyIndex(s.Map,[("a","other")]);
        Assert.False(index.Connects(s.Before.ForwardNode.Uid,s.After.Node.Uid));
        Assert.False(new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1,index)
            .Estimate(Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid)).Ready);
    }
    [Fact]
    public void UnknownIntermediateUidIsNotSilentlyDroppedBetweenPorts()
    {
        var s=Scene();
        Assert.False(new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1,new(s.Map,[("a","b")]))
            .Estimate(Route(s.A.Node.Uid,999,s.B.Node.Uid)).Ready);
    }
    [Fact]
    public void ReversePrefabReferenceAloneIsEnoughWithoutFerryBackReference()
    {
        var s=Scene();s.A.Prefab=null!;s.B.Prefab=null!;
        var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        Assert.True(index.Connects(s.Before.ForwardNode.Uid,s.After.Node.Uid));
        Assert.False(index.Connects(s.A.Node.Uid,s.Before.ForwardNode.Uid));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void PortInterfacesAndActivationNodesAreEstimatedBeforeAndAfterTheSeaLeg(bool reverse)
    {
        var s=Scene();var ids=new[]{s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.A.Node.Uid,
            s.B.Node.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid};
        if(reverse)Array.Reverse(ids);
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1.028,new(s.Map,[("a","b")])).Estimate(Route(ids));
        double expected=300+2*1.028;
        Assert.True(result.Ready,result.Failure);Assert.Equal(expected,result.WorldMeters!.Value,4);
        Assert.Equal(expected/(80*.8/3.6)+60,result.RealSeconds!.Value,4);
    }

    [Fact]
    public void ActivationItemAliasesDoNotAddAnotherTransportMinute()
    {
        var s=Scene();
        var result=new RouteJourneyEstimator(s.Map,(_,_)=>new(80,"category"),1,new(s.Map,[("a","b")]))
            .Estimate(Route(s.Before.Node.Uid,s.Before.ForwardNode.Uid,s.A.Node.Uid,s.A.Uid,
                s.B.Uid,s.B.Node.Uid,s.After.Node.Uid,s.After.ForwardNode.Uid));
        Assert.True(result.Ready,result.Failure);Assert.Equal(302,result.WorldMeters!.Value,4);
        Assert.Equal(302/(80*.8/3.6)+60,result.RealSeconds!.Value,4);
    }

    [Fact]
    public void SamePortPassageNeedsAnExplicitUniqueActivationAndMembership()
    {
        var s=Scene();var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        Assert.True(index.TryPortPassage(s.Before.ForwardNode.Uid,s.A.Node.Uid,out var distance));
        Assert.Equal(1,distance);
        Assert.False(index.TryPortPassage(s.Before.Node.Uid,s.A.Node.Uid,out _));
        Assert.False(index.TryPortPassage(s.Before.ForwardNode.Uid,s.B.Node.Uid,out _));
        s.B.Port=s.A.Port;
        Assert.False(new FerryJourneyIndex(s.Map,[]).TryPortPassage(s.Before.ForwardNode.Uid,s.A.Node.Uid,out _));
    }

    [Fact]
    public void MissingActivationPositionDoesNotRemoveAnExistingCrossPortLink()
    {
        var s=Scene();s.A.Node=null!;
        var index=new FerryJourneyIndex(s.Map,[("a","b")]);
        Assert.True(index.Connects(s.A.Uid,s.B.Uid));
        Assert.False(index.TryPortPassage(s.Before.ForwardNode.Uid,s.A.Uid,out _));
    }
}
