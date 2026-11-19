using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
// Constructed production Runtime handoff; actual recorded SDK suffix evidence is checked separately.
public sealed class WrapperRoundaboutProgressTests
{
    [Fact]
    public void SdkPrefixConsumptionDoesNotRebuildOrReplayTheRingSeries()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var before=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var after=Road.Add(map,new(200,0,0),new(400,0,0),default);
        ulong entry=before.ForwardNode.Uid,exit=after.Node.Uid;
        var passage=new DirectedRoad("prefab:1:0:1",entry,exit,[new(100,0,0),new(180,0,0),new(200,0,0)]){NativeCurveSpans=[new(7,1)]};
        var ring=new RoundaboutStructure("same-ring",[1],[],[],1,[]){RingLength=100,
            EntryPorts=[new(entry,new(100,0,0)){Bearing=0}],
            ExitPorts=[new(exit,new(200,0,0)){Bearing=1,DepartureBranches=[new(new(1,7),new(180,0,0))]}]};
        var nearby=new NearbyMapSnapshot(map,[],[],[]){PrefabPaths=[passage],RoadStructures=new Dictionary<ulong,RoundaboutStructure>{{1,ring}}};
        var full=RouteSnapshot.Parse([new(before.Node.Uid,400,40),new(entry,300,30),new(exit,200,20),new(after.ForwardNode.Uid,0,0)],now);
        var stable=NavigationRouteState.Empty.Accept(full);var logs=new List<string>();var runtime=new NavigationRuntime(log:logs.Add);
        GameDataSnapshot Data(double x)=>new(new(x,0,0,.75,10,400,true,false,1,now),stable.CurrentSnapshot,RouteAvailability.Available,"same-session",true){NavigationRoute=stable};
        var first=runtime.Process(Data(90),nearby,now);int structures=runtime.StructureBuildCount,plans=runtime.EventPlanBuildCount;
        stable=stable.Accept(RouteSnapshot.Parse(full.Nodes.Skip(1).ToArray(),now));
        var same=runtime.Process(Data(90),nearby,now);
        Assert.Equal(1,stable.CurrentNodeIndex);Assert.Same(full,stable.FullRoute);
        Assert.Equal(structures,runtime.StructureBuildCount);Assert.Equal(plans,runtime.EventPlanBuildCount);
        Assert.Equal(first.Upcoming.Select(a=>a.Id),same.Upcoming.Select(a=>a.Id));
        runtime.Process(Data(160),nearby,now);
        Assert.Single(logs.Where(l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u8fdb\u5165\u73af\u5c9b")));
        Assert.Single(logs.Where(l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u9a76\u51fa\u73af\u5c9b")));
    }
    [Fact]
    public void UnloadedMapPrefixKeepsTheSameSeriesInTheProductionRuntime()
    {
        var now=DateTimeOffset.UtcNow;var map=new TruckLib.ScsMap.Map();
        var before=Road.Add(map,Vector3.Zero,new(100,0,0),default);
        var after=Road.Add(map,new(200,0,0),new(400,0,0),default);
        ulong entry=before.ForwardNode.Uid,exit=after.Node.Uid;
        var passage=new DirectedRoad("prefab:1:0:1",entry,exit,[new(100,0,0),new(180,0,0),new(200,0,0)]){NativeCurveSpans=[new(7,1)]};
        var ring=new RoundaboutStructure("same-ring",[1],[],[],1,[]){RingLength=100,
            EntryPorts=[new(entry,new(100,0,0)){Bearing=0}],
            ExitPorts=[new(exit,new(200,0,0)){Bearing=1,DepartureBranches=[new(new(1,7),new(180,0,0))]}]};
        var annotations=new Dictionary<ulong,RoundaboutStructure>{{1,ring}};
        var full=RouteSnapshot.Parse([new(before.Node.Uid,400,40),new(entry,300,30),new(exit,200,20),new(after.ForwardNode.Uid,0,0)],now);
        var stable=NavigationRouteState.Empty.Accept(full);var logs=new List<string>();var runtime=new NavigationRuntime(log:logs.Add);
        GameDataSnapshot Data(double x)=>new(new(x,0,0,.75,10,400,true,false,1,now),stable.CurrentSnapshot,RouteAvailability.Available,"same-session",true){NavigationRoute=stable};
        var original=new NearbyMapSnapshot(map,[],[],[]){PrefabPaths=[passage],RoadStructures=annotations};
        runtime.Process(Data(90),original,now);
        // Same full SDK input, new active geometry window containing only ring and tail.
        var window=new TruckLib.ScsMap.Map();window.MapItems.Add(after.Uid,after);
        window.Nodes.Add(after.Node.Uid,after.Node);window.Nodes.Add(after.ForwardNode.Uid,after.ForwardNode);
        var smaller=new NearbyMapSnapshot(window,[],[],[]){PrefabPaths=[passage],RoadStructures=annotations,
            MissingConnection=_=>new MissingMapSectors([new(9,9)],"test unloaded old prefix")};
        runtime.Process(Data(100),smaller,now);runtime.Process(Data(160),smaller,now);
        Assert.Single(logs,l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u8fdb\u5165\u73af\u5c9b"));
        Assert.Single(logs,l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u9a76\u51fa\u73af\u5c9b"));
        Assert.Same(full,stable.FullRoute);Assert.Equal(0,stable.CurrentNodeIndex);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalSdkIndicesSurviveNormalizationAndKnownGeometryGaps(bool ascending)
    {
        RouteNode[] nodes=[new(10,200,20),new(10,200,20),new(12,100,10),new(13,0,0)];
        if(ascending)Array.Reverse(nodes);
        var sdk=RouteSnapshot.Parse(nodes,DateTimeOffset.UtcNow);
        var a=new DirectedRoad("prefab:1:0:1",10,12,[Vector3.Zero,new(100,0,0)]);
        var b=new DirectedRoad("prefab:2:0:1",12,13,[new(100,0,0),new(200,0,0)]);
        var source=new RoadPathSource(new TruckLib.ScsMap.Map(),[a,b]);
        var component=Assert.Single(source.GetRouteComponents(sdk));
        Assert.Equal(ascending?new[]{2,1}:new[]{0,2},source.SourceNodeIndices(component));
        Assert.Equal(new ulong[]{10,12,13},RouteStructureIdentity.Nodes(sdk));
    }
    [Fact]
    public void ConsumedSdkVisitsKeepTheirMileageButCannotMatchAgain()
    {
        var map=new TruckLib.ScsMap.Map();
        var pass=new DirectedRoad("prefab:1:0:1",10,12,[Vector3.Zero,new(100,0,0)]);
        var back=new DirectedRoad("prefab:2:0:1",12,10,[new(100,0,0),Vector3.Zero]);
        var source=new RoadPathSource(map,[pass,back]);
        var full=RouteSnapshot.Parse([new(10,300,30),new(12,200,20),new(10,100,10),new(12,0,0)],DateTimeOffset.UtcNow);
        var chunk=Assert.Single(source.GetRouteComponents(full));var matcher=new DirectedPathMatcher();
        Assert.Equal(50,matcher.ProjectDistance(new(50,0,0),Vector3.UnitX,chunk));
        int first=source.FirstLiveVisit(chunk,2);Assert.Equal(1,first);
        Assert.Equal(250,matcher.ProjectDistance(new(50,0,0),Vector3.UnitX,chunk,first));
        Assert.Equal(300,chunk.Sum(RouteGeometry.Length));
    }
    [Fact]
    public void RepeatedDirectedRoadMatchesItsFirstQualifiedLiveVisit()
    {
        var pass=new DirectedRoad("pass",10,12,[Vector3.Zero,new(100,0,0)]);
        var back=new DirectedRoad("back",12,10,[new(100,0,0),Vector3.Zero]);
        var matcher=new DirectedPathMatcher();
        Assert.Equal(50,matcher.ProjectDistance(new(50,0,0),Vector3.UnitX,[pass,back,pass],0));
        Assert.Equal(250,matcher.ProjectDistance(new(50,0,0),Vector3.UnitX,[pass,back,pass],1));
    }
    [Fact]
    public void AnotherDirectedRoadWithTheSameGeometryIsStillAmbiguous()
    {
        var pass=new DirectedRoad("pass",10,12,[Vector3.Zero,new(100,0,0)]);
        var back=new DirectedRoad("back",12,10,[new(100,0,0),Vector3.Zero]);
        var other=pass with{Id="different-road"};
        Assert.Null(new DirectedPathMatcher().ProjectDistance(new(50,0,0),Vector3.UnitX,[pass,back,other]));
    }
    [Fact]
    public void ReverseTravelRemainsADifferentDirectedRoad()
    {
        var pass=new DirectedRoad("road",10,12,[Vector3.Zero,new(100,0,0)]);
        var reverse=pass with{StartNodeUid=12,EndNodeUid=10,Points=pass.Points.Reverse().ToArray()};
        var matcher=new DirectedPathMatcher();
        Assert.Equal(50,matcher.ProjectDistance(new(50,0,0),Vector3.UnitX,[pass,reverse,pass]));
        Assert.Equal(150,matcher.ProjectDistance(new(50,0,0),-Vector3.UnitX,[pass,reverse,pass]));
    }

    [Fact]
    public void RuntimeQueuesBothRingVisitsAsSdkConsumptionMovesTheProgressBound()
    {
        var now=DateTimeOffset.UtcNow;var logs=new List<string>();
        var pass=new DirectedRoad("prefab:1:0:1",10,12,[Vector3.Zero,new(80,0,0),new(100,0,0)]){NativeCurveSpans=[new(7,1)]};
        var back=new DirectedRoad("prefab:2:0:1",12,10,[new(100,0,0),Vector3.Zero]);
        var ring=new RoundaboutStructure("same-ring",[1],[],[],1,[]){RingLength=100,
            EntryPorts=[new(10,Vector3.Zero){Bearing=0}],
            ExitPorts=[new(12,new(100,0,0)){Bearing=1,DepartureBranches=[new(new(1,7),new(80,0,0))]}]};
        var nearby=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=[pass,back],
            RoadStructures=new Dictionary<ulong,RoundaboutStructure>{{1,ring}}};
        var full=RouteSnapshot.Parse([new(10,300,30),new(12,200,20),new(10,100,10),new(12,0,0)],now);
        var stable=NavigationRouteState.Empty.Accept(full);var runtime=new NavigationRuntime(log:logs.Add);
        GameDataSnapshot Data(double x)=>new(new(x,0,0,.75,10,300,true,false,1,now),stable.CurrentSnapshot,
            RouteAvailability.Available,"same-session",true){NavigationRoute=stable};
        Assert.NotNull(runtime.Process(Data(0),nearby,now).LaneRoute);
        runtime.Process(Data(60),nearby,now);
        stable=stable.Accept(RouteSnapshot.Parse(full.Nodes.Skip(2).ToArray(),now));
        Assert.Equal(2,stable.CurrentNodeIndex);
        Assert.NotNull(runtime.Process(Data(0),nearby,now).LaneRoute);
        runtime.Process(Data(60),nearby,now);
        Assert.Equal(2,logs.Count(l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u8fdb\u5165\u73af\u5c9b")));
        Assert.Equal(2,logs.Count(l=>l.StartsWith("\u5bfc\u822a\u6307\u4ee4")&&l.Contains("\u9a76\u51fa\u73af\u5c9b")));
    }

}
