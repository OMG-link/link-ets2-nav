using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class MainLoopMapRetryTests
{
    private static readonly MapObjectKey Needed=new(MapObjectKind.Item,99);
    private static NearbyMapSnapshot Window(bool ready=false)
    {
        var map=new TruckLib.ScsMap.Map();if(ready)map.MapItems.Add(Needed.Uid,new Road{Uid=Needed.Uid});
        return new(map,[],[],[]){NativeGraph=new([])};
    }
    private static MapQueryResult<int> Read(MapRetryContext context)=>context.Contains(Needed)?MapQueryResult<int>.Ready(7):
        MapQueryResult<int>.Unavailable(new MissingMapObjects([Needed],"native item"));
    [Fact]
    public void ReadyCallbacksRunSeriallyAfterWindowAdoptionOnTheCallingLoop()
    {
        var owner=new MapRetryCoordinator();owner.Adopt(Window(),1);int calls=0;
        using var handle=owner.Query("one",context=>{calls++;return Read(context);}).Retry!;
        Assert.Equal(1,calls);owner.Adopt(Window(),2);Assert.Equal(1,calls);
        int loop=Environment.CurrentManagedThreadId;
        using var other=owner.Query("two",context=>
        {Assert.Equal(loop,Environment.CurrentManagedThreadId);return Read(context);}).Retry!;
        var ready=Window(true);owner.Adopt(ready,3);Assert.Equal(2,calls);Assert.Equal(7,handle.Capture().Result.Value);
        Assert.True(other.Capture().Result.IsSuccess);
        Assert.Null(owner.Query("ready",Read).Retry);
        owner.Adopt(ready,3);Assert.Equal(2,calls);
    }
    [Fact]
    public void CancellationAndRouteScopeResetInvalidateWaitingLeases()
    {
        var owner=new MapRetryCoordinator();owner.Adopt(Window(),1);
        using var cancel=new CancellationTokenSource();using var handle=owner.Query("cancel",Read,cancel.Token).Retry!;
        cancel.Cancel();Assert.False(handle.Capture().Active);
        using var old=owner.Query("old",Read).Retry!;owner.Reset(false);Assert.False(old.Capture().Active);
        using var current=owner.Query("current",Read).Retry!;owner.Adopt(Window(true),2);Assert.True(current.Capture().Result.IsSuccess);
    }
    [Fact]
    public void OneArrivingKeyRefreshesTheKnownLocalValueWithoutWaitingForDistantKeys()
    {
        var owner=new MapRetryCoordinator();owner.Adopt(Window(),1);int calls=0;
        MapObjectKey[] keys=[Needed,new(MapObjectKind.Item,100)];
        using var handle=owner.Query("partial",context=>
        {
            calls++;var missing=keys.Where(k=>!context.Contains(k)).ToArray();
            return new MapQueryResult<int>(2-missing.Length,missing.Length==0?null:new MissingMapObjects(missing,"partial"));
        }).Retry!;
        var ready=Window(true);owner.Adopt(ready,2);
        Assert.Equal(2,calls);Assert.Equal(1,handle.Capture().Result.Value);
        Assert.Equal([keys[1]],Assert.IsType<MissingMapObjects>(handle.Capture().Result.Issue).Keys);
        owner.Adopt(ready,2);Assert.Equal(2,calls);
    }
    [Fact]
    public void ACallbackFailureIsAnOriginalMainLoopFailureWithoutBackgroundDowngrade()
    {
        var owner=new MapRetryCoordinator();owner.Adopt(Window(),1);var failure=new IOException("query defect");
        using var handle=owner.Query<int>("fault",c=>c.Contains(Needed)?throw failure:Read(c)).Retry!;
        Assert.Same(failure,Assert.Throws<IOException>(()=>owner.Adopt(Window(true),2)));
    }
#if DEBUG
    [Fact]
    public void AlreadyLoadedDependenciesAreOnlyAContractAssertion()
    {
        var owner=new MapRetryCoordinator();owner.Adopt(Window(true),1);
        Assert.Throws<NavigationContractException>(()=>owner.Query<int>("invalid",_=>MapQueryResult<int>.Unavailable(new MissingMapObjects([Needed],"invalid"))));
    }
#endif
    [Fact]
    public void ARealRoadQueryRetainsItsLocalPathThenConsumesTheNewWindowResultAsAWhole()
    {
        var full=new TruckLib.ScsMap.Map();var a=Road.Add(full,Vector3.Zero,new(100,0,0),default);var b=a.Append(new(200,0,0));
        var partial=new TruckLib.ScsMap.Map();partial.MapItems.Add(a.Uid,a);foreach(var n in full.Nodes)partial.Nodes.Add(n.Key,n.Value);
        var route=RouteSnapshot.Parse([new(a.Node.Uid,200,0),new(a.Node.Uid,200,0),new(a.ForwardNode.Uid,100,0),new(b.ForwardNode.Uid,50,0),new(123456,0,0)],DateTimeOffset.UtcNow);
        var owner=new MapRetryCoordinator();var first=new NearbyMapSnapshot(partial,[],[],[]){Retries=owner};owner.Adopt(first,1);
        var old=new RoadPathSource(partial,retries:owner);Assert.Single(Assert.Single(old.GetRouteComponents(route)));
        Assert.Contains(old.MapIssues.OfType<MissingMapObjects>(),m=>m.Keys.Contains(new(MapObjectKind.Item,b.Uid)));
        // Clearing/replacing navigation can repeat the same UID chain in the
        // same map window; its canceled wait must be registered again.
        owner.Reset(false);old.GetRouteComponents(route);Assert.Equal(2,old.RouteStructureBuildCount);
        int lookups=0;
        var next=new NearbyMapSnapshot(full,[],[],[]){Retries=owner,MissingConnection=uid=>{lookups++;return GameDataManager.DescribeMissingNativeObjects(full,uid);}};
        owner.Adopt(next,2);int completedLookups=lookups;old.Dispose();Assert.True(completedLookups>0);
        using var current=new RoadPathSource(full,retries:owner);
        var completed=Assert.Single(current.GetRouteComponents(route));
        Assert.Equal(2,completed.Count);Assert.Equal([0,1],current.SourceNodeIndices(completed));
        Assert.Equal(0,current.FirstLiveVisit(completed,1));Assert.Equal(1,current.FirstLiveVisit(completed,2));
        Assert.Equal(1,current.GetRouteEntry(route,1)!.VisitIndex);
        var stable=NavigationRouteState.Empty.Accept(route).Accept(RouteSnapshot.Parse(route.Nodes.Skip(1),DateTimeOffset.UtcNow));
        Assert.Equal(1,stable.CurrentNodeIndex);Assert.Same(route,stable.FullRoute);
        Assert.Equal(1,current.GetRouteEntry(stable.FullRoute,stable.CurrentNodeIndex)!.VisitIndex);
        Assert.Contains(current.MapIssues.OfType<MissingMapObjects>(),m=>m.Keys.Contains(new(MapObjectKind.Node,123456)));
        Assert.Equal(completedLookups,lookups);Assert.Single(Assert.Single(old.GetRouteComponents(route)));
    }
    [Fact]
    public void RouteQueryCacheDistinguishesInvalidMetricOrderWithoutChangingUidRevision()
    {
        var map=new TruckLib.ScsMap.Map();var a=Road.Add(map,Vector3.Zero,new(100,0,0),default);var b=a.Append(new(200,0,0));
        var valid=RouteSnapshot.Parse([new(a.Node.Uid,200,0),new(a.ForwardNode.Uid,100,0),new(b.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        var mixed=RouteSnapshot.Parse([new(a.Node.Uid,100,0),new(a.ForwardNode.Uid,200,0),new(b.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        Assert.Equal(valid.Revision,mixed.Revision);
        var owner=new MapRetryCoordinator();owner.Adopt(new(map,[],[],[]),1);
        using var source=new RoadPathSource(map,retries:owner);
        Assert.Equal(2,Assert.Single(source.GetRouteComponents(valid)).Count);
        Assert.Empty(source.GetRouteComponents(mixed));
        using var another=new RoadPathSource(map,retries:owner);
        Assert.Empty(another.GetRouteComponents(mixed));
        Assert.Equal(2,Assert.Single(source.GetRouteComponents(valid)).Count);
        Assert.Equal(2,Assert.Single(another.GetRouteComponents(valid)).Count);
    }
    [Fact]
    public void AnExplicitFerryLegIsANormalBreakBetweenTwoKnownDrivableComponents()
    {
        var map=new TruckLib.ScsMap.Map();var before=Road.Add(map,Vector3.Zero,new(100,0,0),default);var after=Road.Add(map,new(100000,0,0),new(100100,0,0),default);
        var a=Ferry.Add(map,new(101,0,0),"a");var b=Ferry.Add(map,new(99999,0,0),"b");
        var pa=new Prefab{Nodes=[before.ForwardNode],Ferry=a};var pb=new Prefab{Nodes=[after.Node],Ferry=b};
        map.MapItems[pa.Uid]=pa;map.MapItems[pb.Uid]=pb;a.Prefab=pa;b.Prefab=pb;
        var ferries=new FerryJourneyIndex(map,[("a","b")]);var owner=new MapRetryCoordinator();
        owner.Adopt(new(map,[],[],[]){Ferries=ferries},1);
        var route=RouteSnapshot.Parse([new(before.Node.Uid,1000,0),new(before.ForwardNode.Uid,900,0),new(after.Node.Uid,100,0),new(after.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        using var source=new RoadPathSource(map,retries:owner,ferries:ferries);
        Assert.Equal(2,source.GetRouteComponents(route).Count);Assert.Empty(source.MapIssues);
    }
    [Fact]
    public void AComponentDependencyDiscoveredAfterRouteMatchingBelongsToThatSameRouteOperation()
    {
        var map=new TruckLib.ScsMap.Map();var road=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var route=RouteSnapshot.Parse([new(road.Node.Uid,100,0),new(road.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        var owner=new MapRetryCoordinator();owner.Adopt(new(map,[],[],[]),1);
        var source=new RoadPathSource(map,retries:owner);Assert.Single(Assert.Single(source.GetRouteComponents(route)));
        source.ReportMapIssue(new MissingMapObjects([Needed],"500m component source"));
        var next=new TruckLib.ScsMap.Map();foreach(var n in map.Nodes)next.Nodes.Add(n.Key,n.Value);foreach(var i in map.MapItems)next.MapItems.Add(i.Key,i.Value);
        next.MapItems.Add(Needed.Uid,new Prefab{Uid=Needed.Uid});owner.Adopt(new(next,[],[],[]),2);source.Dispose();
        using var current=new RoadPathSource(next,retries:owner);Assert.Single(Assert.Single(current.GetRouteComponents(route)));Assert.Empty(current.MapIssues);
    }
}
