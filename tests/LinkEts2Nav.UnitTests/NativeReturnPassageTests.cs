using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NativeReturnPassageTests
{
    private static (Prefab Prefab,PrefabDescriptor Descriptor) NativeReturn(bool connected=true)
    {
        Quaternion back=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI);
        var first=new NavCurve{StartPosition=new(-2,0,0),EndPosition=new(-2,0,-10),Length=10,
            StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity};
        if(connected)first.NextLines.Add(1);
        var middle=new NavCurve{StartPosition=new(-2,0,-10),EndPosition=new(2,0,-10),Length=MathF.PI*2,
            StartRotation=Quaternion.Identity,EndRotation=back,NextLines={2}};
        var last=new NavCurve{StartPosition=new(2,0,-10),EndPosition=new(2,0,0),Length=10,
            StartRotation=back,EndRotation=back};
        return(new Prefab{Uid=42,Nodes=[new Node{Uid=10,Position=Vector3.Zero,Rotation=Quaternion.Identity}]},
            new PrefabDescriptor{Nodes=[new(){Position=Vector3.Zero,Direction=-Vector3.UnitZ,InputLines=[0],OutputLines=[2]}],
                NavCurves=[first,middle,last]});
    }
    private static DirectedRoad Return()
    {
        var (prefab,descriptor)=NativeReturn();return Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
    }

    internal static DirectedRoad ServiceLanes(bool sameInterface,bool detour=false,bool sharedSuffix=false,int longitudinalScale=1)
    {
        var entry=new Node{Uid=10,Position=Vector3.Zero,Rotation=Quaternion.Identity};
        var exit=new Node{Uid=11,Position=new(0,0,-40*longitudinalScale),Rotation=Quaternion.Identity};
        var prefab=new Prefab{Uid=42,Nodes=sameInterface?[entry]:[entry,exit]};
        var descriptor=new PrefabDescriptor();
        NavCurve Straight(Vector3 a,Vector3 b,params int[] next)
        {
            a.Z*=longitudinalScale;b.Z*=longitudinalScale;
            var delta=b-a;
            var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.Atan2(-delta.X,-delta.Z));
            var curve=new NavCurve{StartPosition=a,EndPosition=b,Length=delta.Length(),StartRotation=rotation,EndRotation=rotation};
            foreach(int index in next)curve.NextLines.Add(index);
            return curve;
        }
        descriptor.NavCurves=[sharedSuffix?Straight(new(2,0,0),new(2,0,-20),3):Straight(new(2,0,0),new(2,0,-40))];
        if(detour)
        {
            descriptor.NavCurves.Add(Straight(new(32,0,0),new(32,0,-20),2));
            descriptor.NavCurves.Add(Straight(new(32,0,-20),new(2,0,-20),3));
            descriptor.NavCurves.Add(Straight(new(2,0,-20),new(2,0,-40)));
        }
        else descriptor.NavCurves.Add(Straight(new(32,0,0),new(32,0,-40)));
        int[] outputs=sharedSuffix?[3]:[0,descriptor.NavCurves.Count-1];
        descriptor.Nodes=[new(){Position=Vector3.Zero,Direction=-Vector3.UnitZ,InputLines=[0,1],OutputLines=sameInterface?outputs:[]}];
        if(!sameInterface)descriptor.Nodes.Add(new(){Position=exit.Position,InputLines=[],OutputLines=outputs});
        return Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParallelServiceLanesUsePhysicalMileageBetweenTheirOwnPorts(bool sameInterface)
    {
        var passage=ServiceLanes(sameInterface);var approach=new DirectedRoad("approach",5,10,[new(32,0,10),new(32,0,0)]);
        var onward=new DirectedRoad("onward",passage.EndNodeUid,20,[new(32,0,-40),new(32,0,-70)]);
        DirectedRoad[] route=[approach,passage,onward];var matcher=new DirectedPathMatcher();
        Assert.Equal(40,RouteGeometry.Length(passage),4);
        Assert.Equal(0,passage.NativeCurveSpans[0].PointIndex);
        foreach(var lane in passage.LanePaths)
        {
            float x=lane.Points[0].X;
            foreach(float meters in new[]{0f,8f,20f,32f,40f})
            {
                var vehicle=new Vector3(x,0,-meters);
                Assert.Equal(10+meters,matcher.ProjectDistance(vehicle,-Vector3.UnitZ,route)!.Value,3);
                var camera=matcher.Match(vehicle,-Vector3.UnitZ,new("after",new(32,0,-45),-Vector3.UnitZ),route,true);
                Assert.Equal(CameraApproach.RouteAhead,camera.Approach);
                Assert.Equal(45-meters,camera.RoadDistanceMeters!.Value,3);
            }
        }
        Assert.Equal(55,matcher.ProjectDistance(new(32,0,-45),-Vector3.UnitZ,route)!.Value,3);
        Assert.Null(matcher.ProjectDistance(new(32,0,-20),Vector3.UnitZ,route));
        Assert.Null(matcher.ProjectDistance(new(32,5,-20),-Vector3.UnitZ,route));
        Assert.Null(matcher.ProjectDistance(new(32,0,-41),-Vector3.UnitZ,[passage]));
        var actions=new NavigationManeuverDetector().FindSequence([passage],0,[passage],out double confirmed);
        Assert.DoesNotContain(actions,a=>a.Kind==ManeuverKind.UTurn);
        Assert.Equal(40,confirmed,3);
    }

    [Fact]
    public void CommonSdkInterfaceAndSharedFinalCurveCannotIdentifyMileageAfterALongerDetour()
    {
        var passage=ServiceLanes(true,detour:true);var matcher=new DirectedPathMatcher();
        Assert.Equal(new[]{40d,70d},passage.LanePaths.Select(l=>Math.Round(l.LengthMeters)).Order());
        // At (2,-30) the identical pose is 30 m into the short lane and 60 m into the detour.
        Assert.Null(matcher.ProjectDistance(new(2,0,-30),-Vector3.UnitZ,[passage]));
        Assert.Null(matcher.ProjectDistance(new(32,0,-5),-Vector3.UnitZ,[passage]));
        Assert.Equal(5,matcher.ProjectDistance(new(2,0,-5),-Vector3.UnitZ,[passage])!.Value,3);
    }

    [Fact]
    public void APhysicalReturnChoiceKeepsItsGeometryAndFutureDistanceThroughTheSharedExit()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        var onward=new DirectedRoad("prefab:99:0:1",10,20,[new(2,0,-40),new(42,0,-40)]);
        using var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[passage,onward]);
        var sdk=RouteSnapshot.Parse([new(10,1000,50),new(10,400,20),new(20,0,0)],DateTimeOffset.UnixEpoch);
        var baseline=Assert.Single(source.GetRouteComponents(sdk));
        var (outer,enter)=source.MatchRoutePosition(new(32,0,-5),-Vector3.UnitZ,sdk);
        Assert.Equal(5,enter!.Value,3);Assert.NotSame(baseline,outer);
        Assert.Equal(70,RouteGeometry.Length(outer[0]),3);
        Assert.Equal(new[]{1,2,3},outer[0].NativeCurveSpans.Select(s=>s.CurveIndex));
        Assert.Same(passage.LanePaths,outer[0].LanePaths);
        Assert.Equal(1,Assert.Single(new LaneRoutePlanner().Build(outer.Take(1).ToArray()).Sections).Path.EntryLane);
        var camera=source.MatchCamera(new(32,0,-5),-Vector3.UnitZ,
            new("after-exit",new(17,0,-40),Vector3.UnitX),sdk,RouteAvailability.Available);
        Assert.Equal(CameraApproach.RouteAhead,camera.Approach);Assert.Equal(80,camera.RoadDistanceMeters!.Value,3);
        var (tail,current)=source.MatchRoutePosition(new(2,0,-30),-Vector3.UnitZ,sdk);
        Assert.Same(outer,tail);Assert.Equal(10,RouteGeometry.Length(tail[0])-current!.Value,3);
        var (after,downstream)=source.MatchRoutePosition(new(17,0,-40),Vector3.UnitX,sdk,1);
        Assert.Same(outer,after);Assert.Equal(85,downstream!.Value,3);
        Assert.Equal(40,RouteGeometry.Length(baseline[0]),3);
        Assert.Equal(70,source.GetRouteEntry(sdk,1)!.AlongMeters,3);
        Assert.Same(outer,source.GetRouteEntry(sdk,1)!.Route);

        var (inner,_)=source.MatchRoutePosition(new(2,0,-5),-Vector3.UnitZ,sdk);
        Assert.Equal(40,RouteGeometry.Length(inner[0]),3);
        Assert.Same(outer,source.MatchRoutePosition(new(32,0,-5),-Vector3.UnitZ,sdk).Path);
        source.ResetObservedLanes();
        var (fresh,station)=source.MatchRoutePosition(new(2,0,-30),-Vector3.UnitZ,sdk);
        Assert.Same(baseline,fresh);Assert.Equal(10,RouteGeometry.Length(fresh[0])-station!.Value,3);
    }

    [Fact]
    public void ConsecutiveVisitsToTheSameSdkReturnCanChooseDifferentNativeLanes()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        using var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[passage]);
        var sdk=RouteSnapshot.Parse([new(10,1000,50),new(10,400,20),new(10,0,0)],DateTimeOffset.UnixEpoch);
        var first=source.MatchRoutePosition(new(32,0,-8),-Vector3.UnitZ,sdk);
        Assert.Equal(8,first.AlongMeters!.Value,3);
        var repeated=source.MatchRoutePosition(new(2,0,-8),-Vector3.UnitZ,sdk,1);
        Assert.Equal(1,source.FirstLiveVisit(repeated.Path,1));
        Assert.Equal(new[]{1,0},repeated.Path.Select(e=>e.ObservedLane!.EntryLane));
        Assert.Equal(new[]{0,1},source.SourceNodeIndices(repeated.Path));
        Assert.Equal(78,repeated.AlongMeters!.Value,3);
    }

    [Fact]
    public void AnAmbiguousComponentDoesNotRememberTheObservedBranch()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        var distant=new DirectedRoad("prefab:99:0:1",90,91,[new(200,0,0),new(200,0,-50)]);
        using var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[passage,distant]);
        var sdk=RouteSnapshot.Parse([new(10,600,60),new(10,500,50),new(90,400,40),
            new(91,300,30),new(10,200,20),new(10,0,0)],DateTimeOffset.UnixEpoch);
        var components=source.GetRouteComponents(sdk);
        Assert.Equal(3,components.Count);Assert.False(source.IsSameSdkComponent(components[0],components[2]));
        Assert.Null(source.MatchRoutePosition(new(32,0,-5),-Vector3.UnitZ,sdk).AlongMeters);
        Assert.Null(source.GetRouteEntry(sdk,0)!.Route[0].ObservedLane);
        Assert.Null(source.GetRouteEntry(sdk,4)!.Route[0].ObservedLane);
        var last=source.MatchRoutePosition(new(2,0,-30),-Vector3.UnitZ,sdk,4);
        Assert.Null(last.Path.Single().ObservedLane);
        Assert.Equal(10,RouteGeometry.Length(last.Path[0])-last.AlongMeters!.Value,3);
    }

    [Fact]
    public void ACollinearApproachCannotSelectAFuturePassageBeforeTheTruckReachesIt()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        var approach=new DirectedRoad("prefab:99:0:1",90,10,[new(32,0,10),new(32,0,-10)]);
        using var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[approach,passage]);
        var sdk=RouteSnapshot.Parse([new(90,200,20),new(10,100,10),new(10,0,0)],DateTimeOffset.UnixEpoch);
        source.MatchRoutePosition(new(32,0,-5),-Vector3.UnitZ,sdk);
        Assert.Null(source.GetRouteEntry(sdk,1)!.Route[1].ObservedLane);
        var inside=source.MatchRoutePosition(new(32,0,-17),-Vector3.UnitZ,sdk,1);
        Assert.Equal(1,inside.Path[1].ObservedLane!.EntryLane);
        Assert.Equal(37,inside.AlongMeters!.Value,3);
    }

    [Fact]
    public void ReplacingAPublicComponentEdgeInvalidatesItsObservedGeometry()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        using var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[passage]);
        var sdk=RouteSnapshot.Parse([new(10,100,10),new(10,0,0)],DateTimeOffset.UnixEpoch);
        var published=Assert.Single(source.GetRouteComponents(sdk));
        var observed=source.MatchRoutePosition(new(32,0,-5),-Vector3.UnitZ,sdk);
        Assert.NotNull(observed.Path.Single().ObservedLane);
        var replacement=new DirectedRoad(passage.Id,10,10,[new(2,0,0),new(2,0,-40)]);
        published[0]=replacement;
        Assert.Same(replacement,source.GetRouteEntry(sdk,0)!.Route.Single());
        var current=source.MatchRoutePosition(new(2,0,-5),-Vector3.UnitZ,sdk);
        Assert.Same(replacement,current.Path.Single());Assert.Equal(5,current.AlongMeters!.Value,3);
    }

    [Fact]
    public void NonreturningNativeVisitsUseObservedLanesOnlyForMappedRoundabouts()
    {
        var passage=ServiceLanes(false,detour:true);
        var ring=new RoundaboutStructure("mapped",[42],[],[],1,[]);
        var window=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=[passage]};
        var sdk=RouteSnapshot.Parse([new(10,100,10),new(11,0,0)],DateTimeOffset.UnixEpoch);
        var vehicle=new Vector3(32,0,-5);
        using var ordinary=new RoadPathSource(window);
        Assert.Same(ordinary.GetRouteComponents(sdk)[0],ordinary.GetRouteEntry(sdk,0)!.Route);
        Assert.Null(ordinary.MatchRoutePosition(vehicle,-Vector3.UnitZ,sdk).AlongMeters);
        using var mapped=new RoadPathSource(window with{RoadStructures=new Dictionary<ulong,RoundaboutStructure>{{42,ring}}});
        var projected=mapped.MatchRoutePosition(vehicle,-Vector3.UnitZ,sdk);
        Assert.Equal(5,projected.AlongMeters!.Value,3);
        Assert.Equal(70,RouteGeometry.Length(projected.Path[0]),3);
    }

    [Fact]
    public void SessionChangeOrRouteClearReleasesThePreviouslyObservedNativeBranch()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        var window=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=[passage]};
        var now=DateTimeOffset.UnixEpoch;
        var sdk=RouteSnapshot.Parse([new(10,120,20),new(10,0,0)],now);
        var navigation=NavigationRouteState.Empty.Accept(sdk);var runtime=new NavigationRuntime();
        NavigationDiagnostic Drive(Vector3 point,string session,RouteAvailability availability=RouteAvailability.Available)
        {
            now=now.AddSeconds(1);
            var truck=new VehicleSnapshot(point.X,point.Y,point.Z,0,10,120,true,false,1,now);
            var input=new GameDataSnapshot(truck,sdk,availability,session,true){NavigationRoute=navigation};
            return runtime.Process(input,window,now);
        }
        static int Lane(NavigationDiagnostic result)=>Assert.Single(result.LaneRoute!.Sections).Path.EntryLane;
        Assert.Equal(1,Lane(Drive(new(32,0,-5),"first")));
        Assert.Equal(1,Lane(Drive(new(2,0,-30),"first")));
        Assert.Equal(0,Lane(Drive(new(2,0,-30),"second")));
        Assert.Equal(1,Lane(Drive(new(32,0,-5),"second")));
        Drive(new(2,0,-30),"second",RouteAvailability.None);
        Assert.Equal(0,Lane(Drive(new(2,0,-30),"second")));
    }

    [Fact]
    public void RepeatedSdkVisitsKeepTheirSpeechFrameWhenAnObservedLaneChanges()
    {
        var passage=ServiceLanes(true,detour:true,sharedSuffix:true);
        var ring=new RoundaboutStructure("test-ring",[42],[],[],1,[])
        {
            RingLength=40,EntryPorts=[new(10,Vector3.Zero)],
            ExitPorts=[new(30,new(2,0,-15)){Bearing=1,DepartureBranches=[new(new(42,90),new(2,0,-15))]},
                       new(10,new(2,0,-35)){Bearing=2,DepartureBranches=[new(new(42,91),new(2,0,-35))]}]
        };
        var window=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[])
            {PrefabPaths=[passage],RoadStructures=new Dictionary<ulong,RoundaboutStructure>{{42,ring}}};
        var now=DateTimeOffset.UnixEpoch;
        var sdk=RouteSnapshot.Parse([new(10,240,24),new(10,120,12),new(10,0,0)],now);
        var navigation=NavigationRouteState.Empty.Accept(sdk);
        var guidance=new GuidanceCoordinator();var runtime=new NavigationRuntime(guidance:guidance);
        GuidanceSourcePlan Drive(float x)
        {
            now=now.AddSeconds(1);
            var truck=new VehicleSnapshot(x,0,-5,0,10,180,true,false,1,now);
            var game=new GameDataSnapshot(truck,sdk,sdk.Availability,"session",true){NavigationRoute=navigation};
            Assert.NotNull(runtime.Process(game,window,now).LaneRoute);
            return guidance.Timeline.Sources.Single(s=>s.Source=="navigation");
        }
        var before=Drive(2);var after=Drive(32);
        Assert.Equal(before.CoordinateFrame,after.CoordinateFrame);
        var next=before.Events.Single(e=>e.Id.Contains(":visit:1:")&&e.Id.Contains(":enter:"));
        var moved=after.Events.Single(e=>e.Id==next.Id);
        Assert.Equal(40,next.AlongMeters,3);Assert.Equal(70,moved.AlongMeters,3);
    }

    [Fact]
    public void UnroutedApproachAndExitStillMeetTheMapInterfacesOfAnOffsetNativeLane()
    {
        var map=new TruckLib.ScsMap.Map();
        var approach=Road.Add(map,new(0,0,50),Vector3.Zero,default);
        var onward=Road.Add(map,new(0,0,-40),new(0,0,-90),default);
        var prefab=new Prefab{Uid=42,Nodes=[approach.ForwardNode,onward.Node]};
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new(){Position=Vector3.Zero,InputLines=[0],OutputLines=[]},
                   new(){Position=new(0,0,-40),InputLines=[],OutputLines=[0]}],
            NavCurves=[new(){StartPosition=new(15,0,0),EndPosition=new(15,0,-40),Length=40,
                StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity}]
        };
        var passage=Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.Equal(15,Vector3.Distance(passage.Points[0],passage.EntryInterface));
        Assert.Equal(15,Vector3.Distance(passage.Points[^1],passage.ExitInterface));
        using var paths=new RoadPathSource(map,[passage]);
        var noRoute=RouteSnapshot.Parse([],DateTimeOffset.UnixEpoch);
        var route=paths.GetPath(new(0,0,25),-Vector3.UnitZ,noRoute,RouteAvailability.None);
        Assert.Equal(new[]{approach.Uid.ToString("X16"),passage.Id,onward.Uid.ToString("X16")},route.Select(e=>e.Id));
        var match=new DirectedPathMatcher().Match(new(0,0,25),-Vector3.UnitZ,new("exit",new(0,0,-50),-Vector3.UnitZ),route,false);
        Assert.Equal(75,match.RoadDistanceMeters!.Value,3);
    }

    [Fact]
    public void OwnershipErrorSharesTheDirectedFiniteSegmentAndBendRules()
    {
        Vector3[] points=[Vector3.Zero,new(20,0,0),new(20,0,0),new(20,0,20)];
        var matcher=new DirectedPathMatcher();
        Assert.Equal(0,matcher.ProjectionError(new(5,0,0),Vector3.UnitX,points));
        Assert.Equal(5,matcher.ProjectionError(new(23,0,-4),new(1,0,1),points));
        Assert.Null(matcher.ProjectionError(new(-1,0,0),Vector3.UnitX,points));
        Assert.Null(matcher.ProjectionError(new(5,4,0),Vector3.UnitX,points));
        Assert.Null(matcher.ProjectionError(new(5,0,0),-Vector3.UnitX,points));
        Assert.Equal(14,matcher.ProjectionError(new(5,0,-14),Vector3.UnitX,points,referenceHalfWidthMeters:15));
    }

    [Fact]
    public void OrdinaryGraphViewExcludesReturnsWithoutDiscardingRouteGeometry()
    {
        var straight=new DirectedRoad("prefab:1:0:1",10,20,[Vector3.Zero,Vector3.UnitX]);
        var returning=new DirectedRoad("prefab:1:0:0",10,10,[Vector3.Zero,Vector3.UnitX,Vector3.Zero]);
        var graph=new NativeLaneGraph([straight,returning]);
        Assert.Equal(2,graph.Edges.Count);Assert.Same(straight,Assert.Single(graph.ThroughTraffic.Edges));
        Assert.Same(graph.ThroughTraffic,graph.ThroughTraffic);Assert.Same(graph.ThroughTraffic,graph.ThroughTraffic.ThroughTraffic);
        var ordinary=new NativeLaneGraph([straight]);Assert.Same(ordinary,ordinary.ThroughTraffic);
        Assert.False((returning with{Id="roundabout:known"}).ReturnsToEntry);
    }

    [Fact]
    public void OneControlInterfaceUsesPositiveNativeCurveGeometry()
    {
        var (prefab,descriptor)=NativeReturn();
        var edge=Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));Assert.True(edge.ReturnsToEntry);
        Assert.True(PrefabLaneTopology.Reaches(prefab,descriptor,10,10));
        Assert.Equal(10UL,edge.StartNodeUid);Assert.Equal(10UL,edge.EndNodeUid);
        Assert.Equal(new[]{0,1,2},Assert.Single(edge.LanePaths).CurveIndices);
        Assert.True(RouteGeometry.Length(edge)>25);Assert.True(edge.LanePaths[0].LengthMeters>20);
        Assert.NotEqual(edge.Points[1],edge.Points[^2]);
    }

    [Fact]
    public void MissingNativeChainDoesNotCreateAnEdge()
    {
        var (prefab,descriptor)=NativeReturn(false);Assert.Empty(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.False(PrefabLaneTopology.Reaches(prefab,descriptor,10,10));
    }

    [Fact]
    public void ReturnUsesTheExistingPlayerAccessProjection()
    {
        var (prefab,descriptor)=NativeReturn();descriptor.NavCurves[1].TrafficRule="closed";
        var projected=PrefabPlayerAccess.Build(descriptor,[true],[true],rule=>rule=="closed"?false:null);
        Assert.Empty(PrefabPathGeometry.Build(prefab,projected.Descriptor));
        Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
    }

    [Fact]
    public void SingleInterfacePlacementUsesTheNativeMapRotation()
    {
        var (prefab,descriptor)=NativeReturn();prefab.Nodes[0].Position=new(100,5,200);
        prefab.Nodes[0].Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2);
        Assert.True(PrefabPlacement.TryCreate(prefab,descriptor,out var placement));
        var expected=prefab.Nodes[0].Position+Vector3.Transform(new Vector3(0,0,-10),prefab.Nodes[0].Rotation);
        Assert.True(Vector3.Distance(expected,placement.ToWorld(new(0,0,-10)))<.001);
    }

    [Fact]
    public void ExplicitRepeatedUidConnectsThroughReturnWhileUnroutedContinuationIgnoresIt()
    {
        var returning=Return();
        var approach=new DirectedRoad("prefab:11:0:1",9,10,[new(0,0,10),Vector3.Zero]);
        var onward=new DirectedRoad("prefab:12:0:1",10,11,[Vector3.Zero,new(0,0,-20)]);
        var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[approach,returning,onward]);
        var sdk=RouteSnapshot.Parse([new(9,100,10),new(10,80,8),new(10,40,4),new(11,0,0)],DateTimeOffset.UtcNow);
        Assert.Equal(new[]{approach,returning,onward},Assert.Single(source.GetRouteComponents(sdk)));
        var unplanned=source.GetPath(new(0,0,5),-Vector3.UnitZ,sdk,RouteAvailability.None);
        Assert.Equal(new[]{approach,onward},unplanned);
    }

    [Fact]
    public void OppositeHeadingsDistinguishApproachingFromReturningAlongTheSameRoad()
    {
        var returning=Return();var approach=new DirectedRoad("road",9,10,[new(0,0,10),Vector3.Zero]);
        var reverse=approach with{StartNodeUid=10,EndNodeUid=9,Points=approach.Points.Reverse().ToArray()};
        var matcher=new DirectedPathMatcher();DirectedRoad[] route=[approach,returning,reverse];
        Assert.Equal(5,matcher.ProjectDistance(new(0,0,5),-Vector3.UnitZ,route)!.Value,3);
        Assert.Equal(10+RouteGeometry.Length(returning)+5,matcher.ProjectDistance(new(0,0,5),Vector3.UnitZ,route)!.Value,3);
    }

    [Fact]
    public void OptionalReturnDoesNotChangeOrdinaryForkOrAuxiliaryRecognition()
    {
        var returning=Return();
        var fork=SelectedStraightPassageTests.Fork();var main=fork[0];var side=fork[1];
        var detector=new NavigationManeuverDetector();
        var baseline=Assert.Single(detector.FindSequence([main],0,[main,side]));
        Assert.Equivalent(baseline,Assert.Single(detector.FindSequence([main],0,[main,side,returning])));
        var graph=new NativeLaneGraph([main,side,returning]);var index=AuxiliaryLaneIndex.Build(graph,TestContext.Current.CancellationToken);
        Assert.Empty(index.Corridors);Assert.Same(graph,index.Graph);Assert.Contains(returning,index.Graph.Edges);
    }

    [Fact]
    public void KnownRoundaboutReturnKeepsItsExistingExitNumberInsteadOfBecomingUTurn()
    {
        var returning=Return();double end=RouteGeometry.Length(returning);
        var range=new RoundaboutRouteRange("known",0,1,0,end,
        [new("enter",ManeuverKind.EnterRoundabout,0,returning.Id,returning.Id)
            {EndDistanceMeters=end,SpeechSequenceStartDistanceMeters=0,NearInstruction="进入环岛，请从第四出口驶出"},
         new("exit",ManeuverKind.ExitRoundabout,20,returning.Id,returning.Id)
            {EndDistanceMeters=end,SpeechSequenceStartDistanceMeters=0,ExecutionOnly=true}]);
        var actions=new NavigationManeuverDetector().FindSequence([returning],0,[returning],out _,out _,out _,roundaboutRanges:[range]);
        Assert.Equal(ManeuverKind.EnterRoundabout,actions[0].Kind);
        Assert.Contains("第四出口",actions[0].NearInstruction);
        Assert.DoesNotContain(actions,a=>a.Kind==ManeuverKind.UTurn);
    }

    [Fact]
    public async Task UnrecognizedNativeReturnIsUTurnAndItsRealEndClearsPendingSpeech()
    {
        var returning=Return();double end=RouteGeometry.Length(returning);
        var maneuver=Assert.Single(new NavigationManeuverDetector().FindSequence([returning],0,[returning]));
        Assert.Equal(ManeuverKind.UTurn,maneuver.Kind);Assert.Equal(end,maneuver.EndDistanceMeters);
        var now=DateTimeOffset.UtcNow;var scheduler=new SpeechTriggerScheduler();var timeline=new GuidanceTimeline();
        timeline.Publish(new NavigationEventPlanner().Build("s","r",now,0,[maneuver],true,false,false,scheduler));
        await using var playback=new GuidancePlaybackManager(null,new NeverPlayer(),()=>null);
        playback.ApplyBatch(scheduler.Update(timeline,now,0),false);Assert.Single(playback.Pending);
        timeline.UpdateProgress("navigation","s",end+.1,now.AddSeconds(1),true);
        playback.ApplyBatch(scheduler.Update(timeline,now.AddSeconds(1),0),false);Assert.Empty(playback.Pending);
    }
    private sealed class NeverPlayer:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken token)=>throw new InvalidOperationException("No real audio in this test.");}
}
