using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
using GameMap = TruckLib.ScsMap.Map;

namespace LinkEts2Nav.Map;

/// <summary>A concrete SDK entry in a complete known geometry component.</summary>
public sealed record RouteComponentEntry(IReadOnlyList<DirectedRoad> Route,int VisitIndex,double AlongMeters);

/// <summary>Builds provable directed road paths; unknown prefab geometry is never invented.</summary>
public sealed class RoadPathSource:IDisposable
{
    private sealed record RouteChunks(GameMap Window,List<List<DirectedRoad>> Chunks,
        Dictionary<IReadOnlyList<DirectedRoad>,List<(int Start,int End)>> SdkVisits,
        IReadOnlyList<MapQueryIssue> Issues,IReadOnlyList<string> Diagnostics);
    private RouteChunks CaptureRouteChunks(IReadOnlyList<string> diagnostics)=>new(sourceMap,cachedChunks,
        new(sdkVisits,ReferenceEqualityComparer.Instance),mapIssues.ToArray(),diagnostics);
    private readonly IMapRetryScheduler? retries;
    private readonly FerryJourneyIndex? ferries;
    private MapRetryHandle<RouteChunks>? routeRetry;
    private HashSet<MapObjectKey>? routeKeys;
    private Func<MapRetryContext,MapQueryResult<RouteChunks>>? routeQuery;
    private string? routeOperationKey;
    private long routeWaitScope=-1;
    private readonly Dictionary<(ulong, ulong), Road> links = new();
    private readonly Dictionary<ulong, DirectedRoad> samples = new();
    private readonly Dictionary<ulong, List<Road>> incidentRoads = new();
    private readonly HashSet<(ulong, ulong)> ambiguous = new();
    private readonly Road[] roads;
    private readonly Func<Road,bool,bool?>? playerAccess;
    private readonly Func<Road,double?>? roadHalfWidth;
    internal Func<Road,bool,IReadOnlyList<bool?>?>? RoadLaneAccess { private get; init; }
    private AnchoredRoadLanes? anchoredLanes;
    private readonly Dictionary<(ulong, ulong), DirectedRoad> junctionLinks = new();
    private readonly HashSet<ulong> boundaryNodes = new();
    private readonly DirectedPathMatcher matcher = new();
    private readonly IReadOnlyList<DirectedRoad> prefabGeometry;
    private PrefabLanePositionIndex? prefabPositions;
    private readonly Action<string>? diagnosticLog;
    private readonly Func<ulong,MapQueryIssue>? missingConnection;
    private readonly Action<MapQueryIssue>? reportMapIssue;
    private readonly Action<Node,Node>? validateMissingPrefabPassage;
    private readonly GameMap sourceMap;
    private readonly List<MapQueryIssue> mapIssues=[];
    public IReadOnlyList<MapQueryIssue> MapIssues=>mapIssues;
    public MapQueryIssue DescribeMissingConnection(ulong node)=>missingConnection?.Invoke(node)
        ??GameDataManager.DescribeMissingNativeObjects(sourceMap,node);
    public void ReportMapIssue(MapQueryIssue issue)
    {
        bool Same(MapQueryIssue existing)=>existing.Reason==issue.Reason&&existing.QueryAnchor==issue.QueryAnchor&&(existing,issue) switch
        {
            (MissingMapObjects a,MissingMapObjects b)=>a.Keys.SequenceEqual(b.Keys),
            (MapQueryFailure a,MapQueryFailure b)=>a.Kind==b.Kind,
            _=>false
        };
        if(!mapIssues.Any(Same))
        {
            mapIssues.Add(issue);reportMapIssue?.Invoke(issue);
            if(issue is MissingMapObjects missing&&retries is not null&&routeQuery is not null)
            {
                routeKeys!.UnionWith(missing.Keys);
                var lease=retries.WaitFor(routeOperationKey!,missing,CaptureRouteChunks([]),routeQuery);
                routeRetry?.Dispose();routeRetry=lease;
            }
        }
    }
    private readonly HashSet<(ulong,ulong)> reportedAmbiguities = new();
    private readonly HashSet<(ulong,ulong)> reportedLoadedConnectionFailures = new();
    private ulong[]? cachedNodes;
    private (bool Truncated,ulong? Terminal) cachedRouteMeaning;
    private readonly Dictionary<IReadOnlyList<DirectedRoad>,List<(int Start,int End)>> sdkVisits=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<DirectedRoad>,ObservedRouteLanes> observedLanes=new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyDictionary<ulong,RoundaboutStructure>? rings;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<DirectedRoad>,List<(int Start,int End)>> variantVisits=new();
    public int RouteStructureBuildCount { get; private set; }
    private List<List<DirectedRoad>> cachedChunks = [];
    private IReadOnlyList<DirectedRoad>? navigationGraph;
    private readonly IReadOnlyDictionary<NativeRoadKey,DirectedRoad>? preparedEdges;

    internal RoadPathSource(NearbyMapSnapshot window,Action<string>? log=null,bool retry=true)
        :this(window.Map,window.PrefabPaths,log,window.NativeGraph,
            playerAccess:window.Data is {} data?data.SpeedLimits.RoadPlayerAccess:null,
            missingConnection:window.MissingConnection,retries:retry?window.Retries:null,ferries:window.Ferries)
    {
        rings=window.RoadStructures;
        if(window.Data is {} dataset)
        {
            RoadLaneAccess = dataset.SpeedLimits.RoadLanePlayerAccess;
            validateMissingPrefabPassage=(entry,exit)=>dataset.ValidateMissingPrefabPassage(window.Map,window.Companies,entry,exit);
        }
    }

    public RoadPathSource(GameMap map, IReadOnlyList<DirectedRoad>? junctions = null,Action<string>? log=null,NativeLaneGraph? native=null,Func<Road,bool,bool?>? playerAccess=null,Func<Road,double?>? roadHalfWidth=null,
        Func<ulong,MapQueryIssue>? missingConnection=null,Action<MapQueryIssue>? reportMapIssue=null,IMapRetryScheduler? retries=null,FerryJourneyIndex? ferries=null)
    {
        this.retries=retries;this.ferries=ferries;
        diagnosticLog=log;
        sourceMap=map;
        this.missingConnection=missingConnection;this.reportMapIssue=reportMapIssue;
        this.playerAccess=playerAccess;this.roadHalfWidth=roadHalfWidth;
        navigationGraph=native?.Edges;
        preparedEdges=native?.Edges.ToDictionary(NativeRoadKey.Of);
        roads = map.MapItems.Values.OfType<Road>().Where(r=>CanUse(r,true)||CanUse(r,false)).ToArray();
        foreach (var road in roads)
        {
            if (road.Node is null || road.ForwardNode is null) continue;
            AddIncidentRoad(road.Node.Uid, road);
            AddIncidentRoad(road.ForwardNode.Uid, road);
            if(CanUse(road,true))AddLink((road.Node.Uid, road.ForwardNode.Uid), road);
            if(CanUse(road,false))AddLink((road.ForwardNode.Uid, road.Node.Uid), road);
            boundaryNodes.Add(road.Node.Uid);
            boundaryNodes.Add(road.ForwardNode.Uid);
        }
        var annotated=native?.Edges.Where(e=>NativeLaneGraph.Prefab(e) is not null).ToArray()
            ??new RoadJoinPriorityBuilder(incidentRoads,log,DescribeMissingConnection).Annotate(junctions??[]);
        prefabGeometry = native?.Edges ?? annotated;
        foreach (var group in annotated.GroupBy(p => (p.StartNodeUid, p.EndNodeUid)))
        {
            boundaryNodes.Add(group.Key.StartNodeUid);
            boundaryNodes.Add(group.Key.EndNodeUid);
            if (group.Count() != 1 || links.ContainsKey(group.Key)) ambiguous.Add(group.Key);
            else junctionLinks[group.Key] = group.First();
        }
    }

    private bool CanUse(Road road,bool forward)=>playerAccess?.Invoke(road,forward)!=false;

    private void AddIncidentRoad(ulong uid, Road road)
    {
        if (!incidentRoads.TryGetValue(uid, out var connected))
            incidentRoads[uid] = connected = new();
        connected.Add(road);
    }

    private void AddLink((ulong, ulong) key, Road road)
    {
        if (!links.TryAdd(key, road)) ambiguous.Add(key);
    }

    public IReadOnlyList<DirectedRoad> GetPath(Vector3 position, Vector3 heading, RouteSnapshot route,
        RouteAvailability availability,int currentNodeIndex=0)
    {
        if (availability == RouteAvailability.None) {ResetObservedLanes();return CurrentRoad(position, heading);}
        if (availability != RouteAvailability.Available) return [];
        return MatchRoutePosition(position, heading, route, currentNodeIndex).Path;
    }

    /// <summary>Finds one live SDK route component and the player's coordinate on its published geometry.</summary>
    public (IReadOnlyList<DirectedRoad> Path, double? AlongMeters) MatchRoutePosition(
        Vector3 position, Vector3 heading, RouteSnapshot route, int currentNodeIndex = 0)
    {
        (IReadOnlyList<DirectedRoad> Path, double? AlongMeters) result = ([], null);
        ObservedRouteLanes? selected=null;ObservedRouteLanes.Choice? choice=null;List<(int Start,int End)>? sourceVisits=null;
        foreach (var chunk in GetRouteComponents(route))
        {
            int firstVisit = FirstLiveVisit(chunk, currentNodeIndex);
            if (!IsPositionOnPath(position, chunk, heading, firstVisit))continue;
            IReadOnlyList<DirectedRoad> path=chunk;double? station=null;
            if(observedLanes.TryGetValue(chunk,out var lanes) && lanes.Matches(chunk))
            {
                path=lanes.Current;
                if(lanes.Propose(position,heading,firstVisit,matcher,edge=>IsPositionOnPath(position,[edge],heading,0)) is {} proposal
                    && matcher.ProjectDistance(position,heading,proposal.Path,firstVisit,proposal.Visit) is {} onVisit)
                {
                    selected=lanes;choice=proposal;path=proposal.Path;station=onVisit;sourceVisits=sdkVisits[chunk];
                }
            }
            station??=matcher.ProjectDistance(position,heading,path,firstVisit);
            if(station is not {} along)continue;
            if (result.AlongMeters.HasValue) return ([], null);
            result = (path, along);
        }
        if(selected is not null && choice is not null)
        {
            selected.Accept(choice);
            variantVisits.GetValue(choice.Path,_=>sourceVisits!);
        }
        return result;
    }

    /// <summary>Locates a current SDK entry without clipping its full geometry component.</summary>
    public RouteComponentEntry? GetRouteEntry(RouteSnapshot route,int sourceNodeIndex)
    {
#if DEBUG
        if(sourceNodeIndex<0||sourceNodeIndex>=route.Nodes.Count)
            throw new NavigationContractException("The entry index must belong to the full ingress route.");
#endif
        var components=GetRouteComponents(route);
        foreach(var component in components)
        {
            double along=0;var visits=sdkVisits[component];
            IReadOnlyList<DirectedRoad> path=observedLanes.TryGetValue(component,out var lanes)&&lanes.Matches(component)?lanes.Current:component;
            for(int i=0;i<component.Count;i++)
            {
                if(visits[i].Start==sourceNodeIndex)return new(path,i,along);
                along+=RouteGeometry.Length(path[i]);
            }
        }
        return null;
    }

    /// <summary>Nearby directed geometry, preserving the PPD's explicit junction connections.</summary>
    public IReadOnlyList<DirectedRoad> GetNavigationGraph()
        => navigationGraph ??= roads.Where(r => r.Node is not null && r.ForwardNode is not null
                && !ambiguous.Contains((r.Node.Uid, r.ForwardNode.Uid)))
            .SelectMany(r => (CanUse(r,true)?new[]{Orient(r,r.Node.Uid)}:Array.Empty<DirectedRoad>())
                .Concat(CanUse(r,false)?new[]{Orient(r,r.ForwardNode.Uid)}:Array.Empty<DirectedRoad>()))
            .Concat(junctionLinks.Values).ToArray();
    /// <summary>
    /// Road distance is available only within one continuous route chunk. Across an unknown
    /// prefab, fallback is allowed only when both positions independently match a unique
    /// directed road on the ordered game route and the camera chunk is strictly later.
    /// </summary>
    public CameraCandidate MatchCamera(Vector3 position, Vector3 heading, MonitoredCamera camera,
        RouteSnapshot route, RouteAvailability availability)
    {
        var direct = Vector3.Distance(position, camera.MonitoringPosition);
        CameraCandidate Unknown(string reason = "位置或方向投影不确定") => new(camera.Id, CameraApproach.Unknown, direct) { MatchFailure = reason };
        if (availability == RouteAvailability.None)
        {
            var path = CurrentRoad(position, heading);
            return IsPositionOnPath(camera.MonitoringPosition, path)
                ? matcher.Match(position, heading, camera, path, false)
                : Unknown("无导航：监测点不在当前唯一必经连接链，可能存在分岔");
        }
        if (availability != RouteAvailability.Available) return Unknown();
        IReadOnlyList<IReadOnlyList<DirectedRoad>> chunks = GetRouteChunks(position, route);
        if(observedLanes.Count>0)
        {
            if(MatchRoutePosition(position,heading,route).AlongMeters is null)return Unknown("车辆在当前原生行驶通路上的位置不确定");
            chunks=chunks.Select(chunk=>observedLanes.TryGetValue(chunk,out var variants)&&variants.Matches(chunk)?variants.Current:chunk).ToArray();
        }
        var playerChunks = chunks.Select((path, index) => (Path: path, Index: index))
            .Where(p => IsPositionOnPath(position, p.Path) && matcher.ProjectDistance(position, heading, p.Path).HasValue)
            .ToArray();
        var cameraChunks = chunks.Select((path, index) => (Path: path, Index: index))
            .Where(p => IsPositionOnPath(camera.MonitoringPosition, p.Path)
                && matcher.ProjectDistance(camera.MonitoringPosition, camera.TravelDirection, p.Path).HasValue)
            .ToArray();
        if (playerChunks.Length != 1) return Unknown($"车辆未唯一匹配导航道路或路口（匹配 {playerChunks.Length} 段）");
        if (cameraChunks.Length != 1) return Unknown($"监测点未唯一匹配导航方向（匹配 {cameraChunks.Length} 段）");
        var player = playerChunks[0];
        var monitor = cameraChunks[0];
        if (player.Index == monitor.Index)
            return matcher.Match(position, heading, camera, player.Path, true);
        if (monitor.Index < player.Index) return new(camera.Id, CameraApproach.Behind, direct);
        // The route can visit a physically nearby camera only after a long detour.
        // Unknown gaps do not erase the length of all the already known road pieces.
        double lowerBound = Math.Max(0, player.Path.Sum(PolylineLength)
            - matcher.ProjectDistance(position, heading, player.Path)!.Value);
        for (int i = player.Index + 1; i <= monitor.Index; i++)
        {
            lowerBound += Vector3.Distance(chunks[i - 1][^1].Points[^1], chunks[i][0].Points[0]);
            lowerBound += i == monitor.Index
                ? matcher.ProjectDistance(camera.MonitoringPosition, camera.TravelDirection, monitor.Path)!.Value
                : chunks[i].Sum(PolylineLength);
        }
        return new(camera.Id, CameraApproach.RouteAhead, direct)
        { RouteDistanceLowerBoundMeters = lowerBound };
    }

    private List<List<DirectedRoad>> GetRouteChunks(Vector3 position, RouteSnapshot route) => GetRouteComponents(route);

    /// <summary>Build topology once per directed UID sequence. Position only selects a cached component.</summary>
    public List<List<DirectedRoad>> GetRouteComponents(RouteSnapshot route)
    {
        if(retries is null)return BuildRouteComponents(route);
        var ids=RouteStructureIdentity.Nodes(route);
        var terminal=route.Nodes.MinBy(n=>n.DistanceToEndMeters);
        var meaning=(route.MayBeTruncated,terminal is {DistanceToEndMeters:<=.01f}?terminal.Uid:(ulong?)null);
        if(cachedNodes is not null&&cachedNodes.SequenceEqual(ids)&&cachedRouteMeaning==meaning&&routeWaitScope==retries.ScopeVersion)return cachedChunks;
        routeRetry?.Dispose();
        routeWaitScope=retries.ScopeVersion;mapIssues.Clear();
        var operationKeys=new HashSet<MapObjectKey>();routeKeys=operationKeys;
        routeOperationKey="route-components:"+route.Revision+":"+meaning;
        routeQuery=context=>
        {
            var window=context.Snapshot;
            var diagnostics=new List<string>();
            using var query=new RoadPathSource(window,diagnostics.Add,retry:false);
            var chunks=query.BuildRouteComponents(route);
            var value=query.CaptureRouteChunks(diagnostics);
            var keys=query.MapIssues.OfType<MissingMapObjects>().SelectMany(i=>i.Keys)
                .Concat(operationKeys.Where(k=>!context.Contains(k))).Distinct().ToArray();
            return new MapQueryResult<RouteChunks>(value,keys.Length==0?null:new MissingMapObjects(keys,"Route connections need native lookup keys."));
        };
        var operation=retries.Query(routeOperationKey,routeQuery);
        routeRetry=operation.Retry;
        var result=operation.Result.Value!;
#if DEBUG
        if(!ReferenceEquals(result.Window,sourceMap))throw new NavigationContractException("Route components and the consuming native graph must belong to the same adopted window.");
#endif
        cachedNodes=ids;
        sdkVisits.Clear();foreach(var pair in result.SdkVisits)sdkVisits.Add(pair.Key,pair.Value);
        cachedRouteMeaning=meaning;RouteStructureBuildCount++;cachedChunks=result.Chunks;
        PrepareObservedLanes();
        foreach(var issue in result.Issues)ReportMapIssue(issue);
        foreach(var message in result.Diagnostics)diagnosticLog?.Invoke(message);
        return cachedChunks;
    }
    private List<List<DirectedRoad>> BuildRouteComponents(RouteSnapshot route)
    {
        var ids=RouteStructureIdentity.Nodes(route);
        var terminal=route.Nodes.MinBy(n=>n.DistanceToEndMeters);
        var meaning=(route.MayBeTruncated,terminal is {DistanceToEndMeters:<=.01f}?terminal.Uid:(ulong?)null);
        if(cachedNodes is not null&&cachedNodes.SequenceEqual(ids)&&cachedRouteMeaning==meaning)return cachedChunks;
        cachedNodes=null;cachedChunks=[];sdkVisits.Clear();
        List<DirectedRoad>? chunk = null;
        for (int i = 1; i < ids.Length; i++)
        {
            int sdkStart=i-1;
            var key = (ids[i - 1], ids[i]);
            DirectedRoad? directed = null;
            if(ambiguous.Contains(key)&&reportedAmbiguities.Add(key))
                diagnosticLog?.Invoke($"导航连接解析错误：已加载对象存在重复或冲突连接，入口={key.Item1:X16}，出口={key.Item2:X16}。");
            if (!ambiguous.Contains(key))
            {
                // Exact SDK node UIDs establish route membership. Distance is only
                // a player-location search filter, not a limit on known route tails.
                if (links.TryGetValue(key, out var road)) directed = Orient(road, key.Item1);
                else if (junctionLinks.TryGetValue(key, out var junction)) directed = junction;
                else
                {
                    // An unmatched physical UID is not a PPD curve index or proof of an internal node.
                    // Preserve the legacy verified-span fallback, never skipping a known road boundary.
                    for (int next = i + 1; next < Math.Min(ids.Length, i + 16); next++)
                    {
                        if (boundaryNodes.Contains(ids[next - 1])) break;
                        var span = (ids[i - 1], ids[next]);
                        if (junctionLinks.TryGetValue(span, out junction) && !ambiguous.Contains(span))
                        { directed = junction; i = next; break; }
                    }
                }
            }
            if (directed is null)
            {
                // Verified transport has no ordinary road edge. It remains a
                // normal boundary between the two drivable route components.
                if(ferries?.Connects(key.Item1,key.Item2)==true||ferries?.TryPortPassage(key.Item1,key.Item2,out _)==true)
                {chunk=null;continue;}
                if(ambiguous.Contains(key))ReportMapIssue(new MapQueryFailure(MapQueryFailureKind.Ambiguous,
                    $"已加载导航连接冲突：{key.Item1:X16}/{key.Item2:X16}"));
                else
                {
                    var firstIssue=DescribeMissingConnection(key.Item1);
                    var secondIssue=key.Item2==key.Item1?firstIssue:DescribeMissingConnection(key.Item2);
                    // The unified provider can know a host node outside this
                    // active geometry window. Preserve its exact start anchor.
                    Vector3? anchor=sourceMap.Nodes.TryGetValue(key.Item1,out var seed)?seed.Position:firstIssue.QueryAnchor;
                    ReportMapIssue(firstIssue with{QueryAnchor=anchor});
                    if(key.Item2!=key.Item1)ReportMapIssue(secondIssue with{QueryAnchor=anchor});
                    if(firstIssue is not MissingMapObjects&&secondIssue is not MissingMapObjects
                        &&sourceMap.Nodes.TryGetValue(key.Item1,out var first)&&first is Node entry
                        &&sourceMap.Nodes.TryGetValue(key.Item2,out var second)&&second is Node exit)
                    {
                        validateMissingPrefabPassage?.Invoke(entry,exit);
                        if(reportedLoadedConnectionFailures.Add(key))
                            diagnosticLog?.Invoke($"导航连接解析错误：已加载的 SDK 相邻节点 {key.Item1:X16} → {key.Item2:X16} 无可验证的行驶连接；起点：{firstIssue.Reason}；终点：{secondIssue.Reason}");
                    }
                }
                chunk = null; continue;
            }
            if (chunk is null) { chunk = new(); cachedChunks.Add(chunk);sdkVisits.Add(chunk,[]); }
            chunk.Add(directed);sdkVisits[chunk].Add((sdkStart,i));
        }
        cachedNodes=ids;cachedRouteMeaning=meaning;RouteStructureBuildCount++;
        foreach(var component in cachedChunks)DirectedPathMatcher.RegisterComponent(component);
        PrepareObservedLanes();
        return cachedChunks;
    }
    public void Dispose()=>routeRetry?.Dispose();

    private void PrepareObservedLanes()
    {
        observedLanes.Clear();
        foreach(var component in cachedChunks)
            if(ObservedRouteLanes.Create(component,rings) is {} lanes)observedLanes.Add(component,lanes);
    }

    internal void ResetObservedLanes()
    {
        foreach(var lanes in observedLanes.Values)lanes.Reset();
    }

    private List<(int Start,int End)> Visits(IReadOnlyList<DirectedRoad> component)
        =>variantVisits.TryGetValue(component,out var visits)?visits:sdkVisits[component];

    // A baseline component and each of its measured lane variants share the same SDK visit table.
    internal bool IsSameSdkComponent(IReadOnlyList<DirectedRoad> first,IReadOnlyList<DirectedRoad> second)
        =>ReferenceEquals(Visits(first),Visits(second));

    /// <summary>Stable identities of this component's visits in the untrimmed SDK route.</summary>
    public IReadOnlyList<int> SourceNodeIndices(IReadOnlyList<DirectedRoad> component)
        =>Visits(component).Select(v=>v.Start).ToArray();

    /// <summary>Excludes consumed SDK visits from position matching, preserving full path mileage and geometry.</summary>
    public int FirstLiveVisit(IReadOnlyList<DirectedRoad> component,int currentNodeIndex)
    {
        if(component.Count==0)return 0;
#if DEBUG
        if(cachedNodes is null||currentNodeIndex<0||currentNodeIndex>=cachedNodes.Length)
            throw new NavigationContractException("The progress index must belong to the full ingress route.");
#endif
        var visits=Visits(component);
        int index=visits.FindIndex(v=>v.End>=currentNodeIndex);
        if(index>=0 && index+1<component.Count && visits[index].End==currentNodeIndex
            && component[index].ReturnsToEntry && component[index+1].Id==component[index].Id)index++;
        return index<0?component.Count:index;
    }

    /// <summary>Uses all nearby roads so a parallel non-route road cannot borrow route geometry.</summary>
    public bool IsPositionOnPath(Vector3 position, IReadOnlyList<DirectedRoad> path)
        => IsPositionOnPath(position, path, null, 0);

    private bool IsPositionOnPath(Vector3 position, IReadOnlyList<DirectedRoad> path, Vector3? heading, int firstVisit)
    {
        if (firstVisit >= path.Count || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;
        var nearby = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Score: Distance(position, [Orient(r, r.Node.Uid)])))
            .Where(p => p.Score < LateralTolerance(p.Road)).OrderBy(p => p.Score).ToArray();
        // A nearby clamped road endpoint is not ownership evidence inside a prefab.
        // Apply the same finite-path projection rule used when selecting the current road.
        var candidates = nearby.Where(p => HasProjection(position, Orient(p.Road, p.Road.Node.Uid)))
            .Take(3).ToArray();
        var live = path.Skip(firstVisit);
        if (candidates.Length == 0)
        {
            if (live.Where(p => p.Id.StartsWith("prefab:", StringComparison.Ordinal)
                    && (heading is null || p.LanePaths.Count == 0))
                .Any(p => HasProjection(position, p))) return true;
        }
        if (OwnsOrdinaryRoad()) return true;
        if (heading is not {} direction || !live.Any(edge => edge.LanePaths.Count > 0)) return false;

        prefabPositions ??= PrefabLanePositionIndex.For(prefabGeometry);
        var lanes = prefabPositions.Errors(position, direction, path, firstVisit, matcher);
        // Require separate physical evidence when a permitted parallel lane also fits.
        // The existing three-metre road-reference ambiguity band remains the fallback for
        // roads without a measured width. Native curves sharing an actual lane count once.
        if (!double.IsFinite(lanes.OnRoute) || lanes.OffRoute <= lanes.OnRoute + PrefabLanePositionIndex.LaneSeparation) return false;
        foreach (var (road, _) in nearby)
        {
            var sample = Orient(road, road.Node.Uid);
            var width = sample.ReferenceHalfWidthMeters;
            double footprint = width is > 0 && double.IsFinite(width.Value) ? width.Value : 3;
            bool Competes(bool forward)
            {
                var travel = forward ? direction : -direction;
                if (!CanUse(road, forward) || matcher.ProjectionError(position, travel, sample.Points, width) is not {} error)
                    return false;
                if (error <= lanes.OnRoute + PrefabLanePositionIndex.LaneSeparation) return true;
                if (RoadLaneAccess is not null && error <= lanes.OnRoute + 2 * footprint)
                {
                    anchoredLanes ??= new(incidentRoads, prefabPositions, RoadLaneAccess);
                    if (anchoredLanes.Locate(road, forward, sample.Points.Count - 1) is {} lane
                        && matcher.ProjectionError(position, travel, lane.Midline) is {} centerError)
                    {
                        double nearest = centerError - lane.OffsetUncertainty;
                        if (lane.StableCenter)
                        {
                            double margin = lanes.OnRoute <= PrefabLanePositionIndex.LaneSeparation
                                ? PrefabLanePositionIndex.CoordinateResolution(position) + lanes.SamplingError
                                : PrefabLanePositionIndex.LaneSeparation;
                            return nearest <= lanes.OnRoute + margin;
                        }
                        // Differing anchors bound the possible offsets. Use that bound only when
                        // even the nearest lane lies beyond the reference relative to the player.
                        if (nearest > error + PrefabLanePositionIndex.LaneSeparation)
                            return nearest <= lanes.OnRoute + PrefabLanePositionIndex.LaneSeparation;
                    }
                }
                return error <= lanes.OnRoute + footprint;
            }
            if (Competes(true) || Competes(false)) return false;
        }
        return true;

        bool OwnsOrdinaryRoad()
        {
            bool endpointsOnly = candidates.Length == 0;
            if (endpointsOnly)
            {
                // The outside wedge at a simple two-road bend may have only endpoint projections.
                candidates = nearby.Take(3).ToArray();
                if (candidates.Length < 2) return false;
            }
            if (endpointsOnly || candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3)
            {
                if (candidates.Length > 2 && candidates[2].Score - candidates[0].Score < 3
                    || !TryContinuousJoin(position, candidates[0].Road, candidates[1].Road, out var join))
                    return false;
                var first = candidates[0].Road.Uid.ToString("X16");
                var second = candidates[1].Road.Uid.ToString("X16");
                return live.Zip(live.Skip(1)).Any(pair =>
                    pair.First.EndNodeUid == join && pair.Second.StartNodeUid == join
                    && (pair.First.Id == first && pair.Second.Id == second
                        || pair.First.Id == second && pair.Second.Id == first));
            }
            var selectedId = candidates[0].Road.Uid.ToString("X16");
            return live.Any(road => StringComparer.Ordinal.Equals(road.Id, selectedId)
                && (heading is not {} travel || matcher.ProjectionError(position, travel, road.Points, road.ReferenceHalfWidthMeters).HasValue));
        }
    }

    private bool HasProjection(Vector3 position, DirectedRoad road)
    {
        var nearest = road.Points.Zip(road.Points.Skip(1))
            .Where(p => Vector3.DistanceSquared(p.First, p.Second) > .000001f)
            .OrderBy(p => SegmentDistance(position, p.First, p.Second)).FirstOrDefault();
        var tangent = nearest.Second - nearest.First;
        return matcher.ProjectDistance(position, tangent, [road]).HasValue
            || matcher.ProjectDistance(position, -tangent, [road]).HasValue;
    }

    private IReadOnlyList<DirectedRoad> CurrentRoad(Vector3 position, Vector3 heading)
    {
        if(routeQuery is not null)
        {
            // Switching to no-navigation matching must not attach a new local
            // camera dependency to the previous SDK route operation.
            routeRetry?.Dispose();routeRetry=null;routeQuery=null;routeKeys=null;routeOperationKey=null;
            cachedNodes=null;cachedChunks=[];mapIssues.Clear();
        }
        var candidates = roads.Where(r => r.Node is not null && r.ForwardNode is not null && Near(r, position))
            .Select(r => (Road: r, Sample: Orient(r, r.Node.Uid)))
            // Clamped distance to a road endpoint is not proof that the vehicle has
            // entered that road: it may still be on the junction immediately before it.
            .Where(p => matcher.ProjectDistance(position, heading, [p.Sample]).HasValue
                || matcher.ProjectDistance(position, -heading, [p.Sample]).HasValue)
            .Select(p => (p.Road, p.Sample, Score: Distance(position, [p.Sample])))
            .Where(p => p.Score < LateralTolerance(p.Road)).OrderBy(p => p.Score).Take(3).ToArray();
        if (candidates.Length == 0)
        {
            var junctions = junctionLinks.Values.Where(p => !p.ReturnsToEntry&&!ambiguous.Contains((p.StartNodeUid, p.EndNodeUid))
                && matcher.ProjectDistance(position, heading, [p]).HasValue)
                .Select(p => (Path: p, Error: Distance(position, [p]))).Where(p => p.Error < 12).ToArray();
            // Inside a junction, heading/projection may narrow the legal exits, but
            // several remaining exits are still a real choice without navigation.
            if (junctions.Length == 0 || junctions.Select(p => p.Path.EndNodeUid).Distinct().Count() != 1) return [];
            return Extend(junctions.MinBy(p => p.Error).Path, position);
        }
        var candidate = candidates[0];
        if (candidates.Length > 1 && candidates[1].Score - candidates[0].Score < 3)
        {
            if (candidates.Length > 2 && candidates[2].Score - candidates[0].Score < 3
                || !TryContinuousJoin(position, candidates[0].Road, candidates[1].Road, out var join)) return [];
            // Start with the incoming road so the unique continuation includes both
            // sides of this join. A U-turn or conflicting tangent is not certainty.
            var incoming = candidates.Take(2).Where(p =>
            {
                var atJoin = p.Road.Node.Uid == join ? p.Sample.Points[0] : p.Sample.Points[^1];
                var adjacent = p.Road.Node.Uid == join ? p.Sample.Points[1] : p.Sample.Points[^2];
                return Vector3.Dot(Vector3.Normalize(atJoin - adjacent), heading) >= .5f;
            }).ToArray();
            var outgoing = candidates.Take(2).Where(p =>
            {
                var atJoin = p.Road.Node.Uid == join ? p.Sample.Points[0] : p.Sample.Points[^1];
                var adjacent = p.Road.Node.Uid == join ? p.Sample.Points[1] : p.Sample.Points[^2];
                return Vector3.Dot(Vector3.Normalize(adjacent - atJoin), heading) >= .5f;
            }).ToArray();
            if (incoming.Length != 1 || outgoing.Length != 1 || incoming[0].Road.Uid == outgoing[0].Road.Uid) return [];
            candidate = incoming[0];
        }
        var nearest = candidate.Sample.Points.Zip(candidate.Sample.Points.Skip(1))
            .OrderBy(p => SegmentDistance(position, p.First, p.Second)).First();
        var tangent = Vector3.Normalize(nearest.Second - nearest.First);
        var start = Vector3.Dot(tangent, heading) >= 0 ? candidate.Road.Node.Uid : candidate.Road.ForwardNode.Uid;
        return CanUse(candidate.Road,start==candidate.Road.Node.Uid)?Extend(Orient(candidate.Road,start),position):[];
    }

    private IReadOnlyList<DirectedRoad> Extend(DirectedRoad current, Vector3 position)
    {
        var path = new List<DirectedRoad> { current };
        var visited = new HashSet<ulong>();
        var visitedJunctions = new HashSet<string>(StringComparer.Ordinal);
        if (current.Id.StartsWith("prefab:", StringComparison.Ordinal)) visitedJunctions.Add(current.Id.Split(':')[1]);
        else if (ulong.TryParse(current.Id, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var uid)) visited.Add(uid);
        double coveredAhead = DistanceToEnd(position, current);
        while (coveredAhead < 1000 && path.Count < 128)
        {
            incidentRoads.TryGetValue(current.EndNodeUid, out var connected);
            var successors = (connected ?? []).Where(r => r.Uid.ToString("X16") != current.Id
                &&CanUse(r,r.Node.Uid==current.EndNodeUid)).ToArray();
            if (successors.Length == 0)
            {
                var exits = junctionLinks.Values.Where(p => !p.ReturnsToEntry&&p.StartNodeUid == current.EndNodeUid
                    && !ambiguous.Contains((p.StartNodeUid, p.EndNodeUid))
                    && !visitedJunctions.Contains(p.Id.Split(':')[1])).ToArray();
                if (exits.Length != 1)
                {
                    if(exits.Length==0)ReportMapIssue(DescribeMissingConnection(current.EndNodeUid)
                        with{QueryAnchor=current.Points[^1]});
                    break;
                }
                var junction = exits[0];
                if (Vector3.Distance(current.ExitInterface, junction.EntryInterface) > 2) break;
                visitedJunctions.Add(junction.Id.Split(':')[1]);
                path.Add(junction);
                coveredAhead += PolylineLength(junction);
                current = junction;
                continue;
            }
            // Only a unique Road continuation can extend the proof of inevitability.
            if (successors.Length != 1 || !visited.Add(successors[0].Uid)) break;
            var next = Orient(successors[0], current.EndNodeUid);
            if (ambiguous.Contains((next.StartNodeUid, next.EndNodeUid))
                || Vector3.Distance(current.ExitInterface, next.EntryInterface) > 2) break;
            path.Add(next);
            coveredAhead += PolylineLength(next);
            current = next;
        }
        return path;
    }

    private bool TryContinuousJoin(Vector3 position, Road first, Road second, out ulong join)
    {
        join = 0;
        var shared = new[] { first.Node, first.ForwardNode }
            .Where(node => node.Uid == second.Node.Uid || node.Uid == second.ForwardNode.Uid).ToArray();
        if (shared.Length != 1 || Vector3.Distance(position, shared[0].Position) > 12
            || !incidentRoads.TryGetValue(shared[0].Uid, out var connected) || connected.Count != 2)
            return false;
        join = shared[0].Uid;
        return true;
    }

    private static double PolylineLength(DirectedRoad road)
        => road.Points.Zip(road.Points.Skip(1)).Sum(p => (double)Vector3.Distance(p.First, p.Second));

    private static double DistanceToEnd(Vector3 position, DirectedRoad road)
    {
        double offset = 0, bestAlong = 0, bestError = double.PositiveInfinity;
        foreach (var (a, b) in road.Points.Zip(road.Points.Skip(1)))
        {
            var delta = b - a;
            var length = delta.Length();
            if (length < .001f) continue;
            var t = Math.Clamp(Vector3.Dot(position - a, delta) / delta.LengthSquared(), 0, 1);
            var error = Vector3.Distance(position, a + delta * t);
            if (error < bestError) { bestError = error; bestAlong = offset + length * t; }
            offset += length;
        }
        return offset - bestAlong;
    }

    private static bool Near(Road road, Vector3 position)
        => Vector3.Distance(road.Node.Position, position) <= 800 || Vector3.Distance(road.ForwardNode.Position, position) <= 800;

    private DirectedRoad Orient(Road road, ulong start)
    {
        if(preparedEdges is not null&&preparedEdges.TryGetValue(new(road.Uid.ToString("X16"),start,
            start==road.Node.Uid?road.ForwardNode.Uid:road.Node.Uid),out var prepared))return prepared;
        if (!samples.TryGetValue(road.Uid, out var sample))
        {
            int count = Math.Clamp((int)Math.Ceiling(road.Length / 8), 16, 256);
            var points = Enumerable.Range(0, count + 1).Select(i => road.InterpolateCurve((float)i / count).Position).ToArray();
            sample = new(road.Uid.ToString("X16"), road.Node.Uid, road.ForwardNode.Uid, points)
            { RoadType = road.RoadType.ToString(),ReferenceHalfWidthMeters=roadHalfWidth?.Invoke(road) };
            samples.Add(road.Uid, sample);
        }
        return start == sample.StartNodeUid ? sample : sample with { StartNodeUid = sample.EndNodeUid, EndNodeUid = sample.StartNodeUid, Points = sample.Points.Reverse().ToArray() };
    }

    private double LateralTolerance(Road road)
    {
        var width=Orient(road,road.Node.Uid).ReferenceHalfWidthMeters;
        return width is {} w&&double.IsFinite(w)&&w>0?Math.Max(12,w):12;
    }

    private static double Distance(Vector3 point, IReadOnlyList<DirectedRoad> path)
        => path.SelectMany(r => r.Points.Zip(r.Points.Skip(1))).Select(p => SegmentDistance(point, p.First, p.Second))
            .DefaultIfEmpty(double.PositiveInfinity).Min();

    private static double SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var delta = b - a;
        float t = delta.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, delta) / delta.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + delta * t);
    }
}
