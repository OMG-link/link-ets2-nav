using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class MapQueryScopeIntegrationTests
{
    [Fact]
    public async Task DistantSdkGapIsRecordedThenActivatedWhenTheRealRequestScopeApproaches()
    {
        var full=new TruckLib.ScsMap.Map();
        var near=Road.Add(full,new(-100,0,0),new(100,0,0),default);
        var middle=near.Append(new(15000,0,0));
        var far=middle.Append(new(15100,0,0));
        var partial=new TruckLib.ScsMap.Map();
        partial.MapItems[middle.Uid]=middle;
        foreach(var node in full.Nodes)partial.Nodes.Add(node.Key,node.Value);
        var route=RouteSnapshot.Parse([new(near.Node.Uid,2000,0),new(middle.Node.Uid,1500,0),
            new(middle.ForwardNode.Uid,1000,0),new(far.ForwardNode.Uid,0,0)],DateTimeOffset.UtcNow);
        var nearOwner=new SectorCoordinate(0,0);var farOwner=new SectorCoordinate(3,0);
        var requests=new List<SectorCoordinate[]>();
        var nearNotified=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var farNotified=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var retries=new MapRetryCoordinator(sectors=>
        {
            lock(requests)requests.Add(sectors.ToArray());
            if(sectors.Contains(nearOwner))nearNotified.TrySetResult();
            if(sectors.Contains(farOwner))farNotified.TrySetResult();
        });
        NearbyMapSnapshot Snapshot(Vector3 position,TruckLib.ScsMap.Map map,params SectorCoordinate[] ready)=>
            new(map,[],[],ready){ReadySectors=ready,Coverage=MapScanCoverage.Create(position,1000,null,null,0)};
        retries.Publish(Snapshot(Vector3.Zero,partial),1);
        var paths=new RoadPathSource(partial,missingConnection:uid=>new MissingMapSectors(
            [full.Nodes[uid].Position.X>10000?farOwner:nearOwner],$"native boundary {uid:X16}"),
            reportMapIssue:retries.Report);
        paths.GetRouteComponents(route);
        await nearNotified.Task.WaitAsync(TimeSpan.FromSeconds(2),TestContext.Current.CancellationToken);
        Assert.Contains(paths.MapIssues.OfType<MissingMapSectors>(),m=>m.QueryAnchor!.Value.X==15000);
        lock(requests)Assert.DoesNotContain(requests,sectors=>sectors.Contains(farOwner));

        retries.Publish(Snapshot(new(15000,0,0),partial),2);
        paths.ReconsiderMapIssues();
        await farNotified.Task.WaitAsync(TimeSpan.FromSeconds(2),TestContext.Current.CancellationToken);
        lock(requests)Assert.Contains(requests,sectors=>sectors.Contains(farOwner));
        Assert.Equal(1,paths.RouteStructureBuildCount);
        var missing=paths.MapIssues.OfType<MissingMapSectors>().First(m=>m.QueryAnchor!.Value.X==15000);
        int invoked=0;
        using var handle=retries.Register("far-sdk-connection",missing,(context,token)=>
        {
            Interlocked.Increment(ref invoked);
            var rebuilt=new RoadPathSource(context.Snapshot.Map);
            bool present=rebuilt.GetRouteComponents(route).SelectMany(c=>c).Any(e=>e.Id==far.Uid.ToString("X16"));
            return ValueTask.FromResult(MapQueryResult<bool>.Ready(present));
        },TestContext.Current.CancellationToken);
        await Task.Delay(30,TestContext.Current.CancellationToken);
        Assert.Equal(0,Volatile.Read(ref invoked));
        retries.Publish(Snapshot(new(15000,0,0),full,nearOwner,farOwner),3);
        for(int i=0;i<200&&!handle.Capture().Result.IsSuccess;i++)
            await Task.Delay(10,TestContext.Current.CancellationToken);
        var result=handle.Capture();
        Assert.True(result.Result.IsSuccess);
        Assert.True(result.Result.Value);
        Assert.Equal(3,result.Revision);
        Assert.Equal(1,Volatile.Read(ref invoked));
    }
}
