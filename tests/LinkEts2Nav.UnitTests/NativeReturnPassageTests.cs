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
        var edge=Return();Assert.True(edge.ReturnsToEntry);
        Assert.Equal(10UL,edge.StartNodeUid);Assert.Equal(10UL,edge.EndNodeUid);
        Assert.Equal(new[]{0,1,2},Assert.Single(edge.LanePaths).CurveIndices);
        Assert.True(RouteGeometry.Length(edge)>25);Assert.True(edge.LanePaths[0].LengthMeters>20);
        Assert.NotEqual(edge.Points[1],edge.Points[^2]);
    }

    [Fact]
    public void MissingOrZeroLengthNativeChainDoesNotCreateAnEdge()
    {
        var (prefab,descriptor)=NativeReturn(false);Assert.Empty(PrefabPathGeometry.Build(prefab,descriptor));
        foreach(var curve in descriptor.NavCurves)curve.Length=0;
        Assert.Empty(PrefabPathGeometry.Build(prefab,descriptor));
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
        var info=new RoadForkInfo(10,2,[new(20,ForkSignal.None,[0],[0],[],true),new(30,ForkSignal.None,[1],[1],[],true)]);
        var main=new DirectedRoad("prefab:000000000000002A:0:1",10,20,[Vector3.Zero,new(0,0,-20)]){ForkInfo=info};
        var side=new DirectedRoad("prefab:000000000000002A:0:2",10,30,[Vector3.Zero,new(20,0,-20)]){ForkInfo=info};
        var detector=new NavigationManeuverDetector();
        var baseline=Assert.Single(detector.FindSequence([main],0,[main,side]));
        Assert.Equivalent(baseline,Assert.Single(detector.FindSequence([main],0,[main,side,returning])));
        var graph=new NativeLaneGraph([main,side,returning]);var index=AuxiliaryLaneIndex.Build(graph,TestContext.Current.CancellationToken);
        Assert.Empty(index.Corridors);Assert.Same(graph,index.Graph);Assert.Contains(returning,index.Graph.Edges);
    }

    [Fact]
    public void KnownRoundaboutReturnKeepsItsExistingExitNumberInsteadOfBecomingUTurn()
    {
        var returning=Return() with{Roundabout=new("known",4){Exits=[new(4,20)]}};
        var actions=new NavigationManeuverDetector().FindSequence([returning],0,[returning]);
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
