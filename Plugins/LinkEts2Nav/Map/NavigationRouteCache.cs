using LinkEts2Nav.Runtime;
using NativeMap=TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

internal sealed record LoadedNavigationRoute(RouteSnapshot Source,NativeMap Map,RouteJourneyEstimator Estimator)
{
    internal static bool Covers(RouteSnapshot loaded,RouteSnapshot requested)
    {
        if(requested.Availability!=RouteAvailability.Available)return false;
        int offset=loaded.Nodes.Count-requested.Nodes.Count;
        if(offset<0)return false;
        if(loaded.Revision==requested.Revision)return true;
        // The SDK normally trims nodes already driven. Their tail is already available.
        for(int i=0;i<requested.Nodes.Count;i++)
            if(loaded.Nodes[i+offset].Uid!=requested.Nodes[i].Uid)return false;
        return true;
    }

    internal RouteJourneyEstimate? Estimate(RouteSnapshot requested)
        =>Covers(Source,requested)?Estimator.Estimate(requested):null;
}

/// <summary>Loads only the latest trip on a background task while local scans continue.</summary>
internal sealed class NavigationRouteCache(GameDataManager data,string indexFile,Action refresh) : IDisposable
{
    private readonly CancellationTokenSource stop=new();
    private Task<LoadedNavigationRoute>? loading;
    private CancellationTokenSource? routeStop;
    private RouteSnapshot? loadingRoute;
    private LoadedNavigationRoute? loaded;
    private NavigationSectorIndex? index;
    private FerryJourneyIndex? ferries;

    // Only the map scanner calls this method and publishes completed results.
    internal LoadedNavigationRoute? Resolve(RouteSnapshot? route)
    {
        if(loading is {IsCompleted:true} completed)
        {
            try
            {
                var result=completed.GetAwaiter().GetResult();
                if(route is not null&&LoadedNavigationRoute.Covers(result.Source,route))loaded=result;
            }
            catch(OperationCanceledException) when(routeStop?.IsCancellationRequested==true) { }
            finally
            {
                loading=null;
                loadingRoute=null;
                routeStop?.Dispose();
                routeStop=null;
                if(ferries is not null)data.PublishFerries(ferries);
            }
        }
        if(route is not {Availability:RouteAvailability.Available})
        {
            routeStop?.Cancel();
            loaded=null;
            return null;
        }
        if(loaded is {} available&&LoadedNavigationRoute.Covers(available.Source,route))return available;
        loaded=null;
        if(loadingRoute is {} inFlight)
        {
            if(!LoadedNavigationRoute.Covers(inFlight,route))routeStop?.Cancel();
            return null;
        }
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        routeStop=cancellation;
        loadingRoute=route;
        loading=Task.Run(()=>Read(route,cancellation.Token));
        _=loading.ContinueWith(_=>refresh(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return null;
    }

    private LoadedNavigationRoute Read(RouteSnapshot route,CancellationToken token)
    {
        var files=data.SequentialMapReads();
        // A replan can supersede the route without restarting index construction.
        index??=NavigationSectorIndex.OpenOrBuild(files,data.MapPath,indexFile,stop.Token);
        ferries??=FerryJourneyIndex.Read(data,new GameMapData(NavigationRouteMap.ReadFerries(files,data.MapPath,index,stop.Token)),
            stop.Token,data.SpeedLimits.ReferenceHalfWidth);
        token.ThrowIfCancellationRequested();
        var map=NavigationRouteMap.ReadRoute(files,data.MapPath,index,route,token);
        var estimator=data.CreateRouteEstimator(map,ferries,route,token);
        token.ThrowIfCancellationRequested();
        return new(route,map,estimator);
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loading?.GetAwaiter().GetResult(); }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        finally
        {
            routeStop?.Dispose();
            index?.Dispose();
            stop.Dispose();
        }
    }
}
