using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;
public sealed class RoundaboutNavigationTests
{
    private static RoundaboutStructure Ring(bool mirrored=false)
    {
        Vector3 P(double a)=>new((float)(20*Math.Cos(a)),0,(float)((mirrored?20:-20)*Math.Sin(a)));
        var graph=new Dictionary<RoundaboutCurveKey,RoundaboutCurve>();
        var ring=Enumerable.Range(0,8).Select(i=>new RoundaboutCurveKey(1,i)).ToArray();
        for(int i=0;i<8;i++)
        {
            var next=new List<RoundaboutCurveKey>{ring[(i+1)%8]};
            if(i%2==1)next.Add(new(1,100+((i+1)%8)/2));
            graph[ring[i]]=new(ring[i],1,false,[P(i*Math.PI/4),P((i+.5)*Math.PI/4),P((i+1)*Math.PI/4)],next);
        }
        for(int n=0;n<4;n++)
        {
            var boundary=P(n*Math.PI/2)*2;
            graph[new(1,200+n)]=new(new(1,200+n),1,false,[P(n*Math.PI/2+.04)*2,P((2*n+1)*Math.PI/4)],[ring[2*n+1]],(ulong)(10+n));
            graph[new(1,100+n)]=new(new(1,100+n),1,false,[P(n*Math.PI/2),boundary],[new((ulong)(1000+n),0)],0,(ulong)(10+n));
            graph[new((ulong)(1000+n),0)]=new(new((ulong)(1000+n),0),(ulong)(1000+n),false,[boundary,boundary*2],[],(ulong)(100+n));
        }
        var ports=RoundaboutPorts.Build(graph,ring,[1]);
        return new("ring",[1],ring,[],1,[]){EntryPorts=ports.Entries,ExitPorts=ports.Exits,RingLength=ports.Length};
    }
    [Theory]
    [InlineData(0,1,1)] [InlineData(0,2,2)] [InlineData(0,3,3)] [InlineData(0,0,4)]
    [InlineData(2,3,1)] [InlineData(2,0,2)]
    public void CountsPhysicalExitsFromSelectedEntryIncludingReturnToOriginalArm(int entry,int exit,int ordinal)
    {
        var ring=Ring();
        var route=new[]{new DirectedRoad("prefab:1:0:1",(ulong)(10+entry),(ulong)(10+exit),[new(0,0,0),new(100,0,0)])};
        var adapted=RoundaboutRouteAdapter.Apply(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring}});
        Assert.Equal(ordinal,Assert.Single(adapted).Roundabout!.ExitNumber);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PolarOrderingUsesSeparatePhysicalEntryAndExitEndpoints(bool mirrored)
    {
        var ring=Ring(mirrored);
        var road=new DirectedRoad("prefab:1:0:3",10,13,[Vector3.Zero,new(100,0,0)]);
        var adapted=Assert.Single(RoundaboutRouteAdapter.Apply([road],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        Assert.Equal(3,adapted.Roundabout!.ExitNumber);
        Assert.Equal(new[]{1,2,3},adapted.Roundabout.Exits.Select(e=>e.Number));
        Assert.NotEqual(ring.EntryPorts.Single(p=>p.NodeUid==10).Bearing,
            ring.ExitPorts.Single(p=>p.BoundaryNodeUid==10).Bearing);
    }

    [Fact]
    public void CompositePassagePreservesActualGeometryAndUsesOnePhysicalDecision()
    {
        var ring=Ring() with{PrefabUids=[1,2]};
        DirectedRoad[] route=[new("prefab:1:0:1",10,50,[new(40,0,0),new(20,0,0),new(0,0,-20)]),
            new("prefab:2:0:1",50,12,[new(0,0,-20),new(-20,0,0),new(-40,0,0)])];
        var adapted=RoundaboutRouteAdapter.Apply(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring},{2,ring}});
        var edge=Assert.Single(adapted);Assert.Equal(2,edge.Roundabout!.ExitNumber);
        Assert.Equal(route.SelectMany(e=>e.Points),edge.Points);
        var detector=new NavigationManeuverDetector();var maneuvers=detector.FindSequence(adapted,0,[]);
        Assert.Equal(new[]{ManeuverKind.EnterRoundabout,ManeuverKind.PassRoundaboutExit,ManeuverKind.ExitRoundabout},maneuvers.Select(m=>m.Kind));
        double length=route.Sum(RouteGeometry.Length);
        Assert.All(maneuvers, m => Assert.Equal(length, m.EndDistanceMeters));
        Assert.Equal("进入环岛",maneuvers[0].Instruction);
        Assert.Equal("进入环岛，请从第二出口驶出",maneuvers[0].NearInstruction);
        Assert.Equal(length-20,maneuvers[^1].DistanceMeters,3);
        Assert.Equal(20,edge.Roundabout.ExitConnectorMeters,3);
    }
    [Fact]
    public void ASpatiallyCloserOtherLaneDepartureCannotMoveTheSelectedExitToTheEntrance()
    {
        var ring=Ring();
        ring=ring with{ExitPorts=ring.ExitPorts.Select(p=>p.BoundaryNodeUid==12?p with
        {
            DepartureBranches=[new(new(1,999),Vector3.Zero),new(new(1,7),new(80,0,5))]
        }:p).ToArray()};
        var selected=new DirectedRoad("prefab:1:0:2",10,12,[Vector3.Zero,new(40,0,0),new(80,0,0),new(100,0,0)])
            {NativeCurveSpans=[new(4,0),new(7,2)]};
        var adapted=Assert.Single(RoundaboutRouteAdapter.Apply([selected],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        Assert.Equal(80,adapted.Roundabout!.Exits[^1].AlongMeters);
        Assert.Equal(20,adapted.Roundabout.ExitConnectorMeters);
    }
    [Fact]
    public void ExitOnlyHasTwentyMeterExecutionCueAndIsPreparedBeforeItIsNeeded()
    {
        var now=DateTimeOffset.UtcNow;var policy=new NavigationInstructionPolicy();
        var exit=new NavigationManeuver("exit",ManeuverKind.ExitRoundabout,150,"in","out"){ExecutionOnly=true,EndDistanceMeters=180};
        Assert.Equal(new[]{"驶出环岛。"},policy.Plan(exit,"s",now,now,true));
        Assert.Empty(policy.Update(exit,"s",now,now,true));
        now=now.AddSeconds(1);exit=exit with{DistanceMeters=100,EndDistanceMeters=130};
        Assert.Empty(policy.Update(exit,"s",now,now,true));
        now=now.AddSeconds(1);exit=exit with{DistanceMeters=20,EndDistanceMeters=50};
        Assert.Equal("驶出环岛。",Assert.Single(policy.Update(exit,"s",now,now,true)));
        Assert.Empty(policy.Update(exit,"s",now,now,true));
    }
    [Fact]
    public void UnknownPortLeavesExistingGeometryUnchanged()
    {
        var ring=Ring();var edge=new DirectedRoad("prefab:1:0:1",999,12,[Vector3.Zero,Vector3.UnitX]);
        Assert.Same(edge,Assert.Single(RoundaboutRouteAdapter.Apply([edge],new Dictionary<ulong,RoundaboutStructure>{{1,ring}})));
    }

    [Fact]
    public void ExitAudioIsPreparedWhileStillApproachingEntrance()
    {
        var now=DateTimeOffset.UtcNow;var policy=new NavigationInstructionPolicy();
        NavigationManeuver[] actions=[
            new("enter",ManeuverKind.EnterRoundabout,200,"in","out")
                {JunctionId="ring",EndDistanceMeters=350,SpeechSequenceId="ring",SpeechSequenceStartDistanceMeters=200,Instruction="进入环岛，从第二出口驶出"},
            new("exit",ManeuverKind.ExitRoundabout,300,"in","out")
                {JunctionId="ring",EndDistanceMeters=350,SpeechSequenceId="ring",SpeechSequenceStartDistanceMeters=200,SpeechSequenceOrder=2,ExecutionOnly=true}];
        var plan=policy.PlanSequence(actions,"s",now,now,true);
        Assert.Contains("驶出环岛。",plan);
        Assert.DoesNotContain(plan,t=>t.Contains("米后，从此出口"));
    }

    [Fact]
    public void ExitExecutionUsesRingDepartureRatherThanOuterPrefabBoundary()
    {
        var edge=new DirectedRoad("ring",10,20,[Vector3.Zero,new(100,0,0)])
            {Roundabout=new("ring",2,30)};
        var detector=new NavigationManeuverDetector();
        var actions=detector.FindSequence([edge],50,[]);
        var exit=Assert.Single(actions);
        Assert.Equal(ManeuverKind.ExitRoundabout,exit.Kind);
        Assert.Equal(20,exit.DistanceMeters);
    }

    [Fact]
    public void NumberedExitIsOnlyAddedAtHundredMetersAndEntryExecution()
    {
        var now=DateTimeOffset.UtcNow;var policy=new NavigationInstructionPolicy();
        var target=new NavigationManeuver("enter",ManeuverKind.EnterRoundabout,500,"in","out")
            {EndDistanceMeters=700,Instruction="进入环岛",NearInstruction="进入环岛，请从第三出口驶出"};
        Assert.Equal("五百米后，进入环岛。",Assert.Single(policy.Update(target,"s",now,now,true)));
        now=now.AddSeconds(1);target=target with{DistanceMeters=200};
        Assert.Equal("两百米后，进入环岛。",Assert.Single(policy.Update(target,"s",now,now,true)));
        now=now.AddSeconds(1);target=target with{DistanceMeters=100};
        Assert.Equal("一百米后，进入环岛，请从第三出口驶出。",Assert.Single(policy.Update(target,"s",now,now,true)));
        now=now.AddSeconds(1);target=target with{DistanceMeters=20};
        Assert.Equal("进入环岛，请从第三出口驶出。",Assert.Single(policy.Update(target,"s",now,now,true)));
    }
    [Fact]
    public void EveryDepartureGetsItsOwnIndependentTwentyMeterCue()
    {
        var now=DateTimeOffset.UtcNow;var policy=new NavigationInstructionPolicy();
        var edge=new DirectedRoad("ring",10,20,[Vector3.Zero,new(200,0,0)])
            {Roundabout=new("ring",3){Exits=[new(1,50),new(2,100),new(3,150)]}};
        var detector=new NavigationManeuverDetector();
        var before=detector.FindSequence([edge],0,[]);
        var plan=policy.PlanSequence(before,"s",now,now,true);
        Assert.Contains("经过第一出口。",plan);Assert.Contains("经过第二出口。",plan);Assert.Contains("驶出环岛。",plan);
        foreach(var (along,text) in new[]{(30,"经过第一出口。"),(80,"经过第二出口。"),(130,"驶出环岛。")})
        {
            now=now.AddSeconds(1);var actions=detector.FindSequence([edge],along,[]);
            Assert.Equal(20,actions[0].DistanceMeters);
            Assert.Equal(text,Assert.Single(policy.UpdateSequence(actions,"s",now,now,true)));
            Assert.Empty(policy.UpdateSequence(actions,"s",now,now,true));
        }
    }

    [Fact]
    public void IntermediateDepartureProjectsOntoActualRouteRatherThanUsingRingDistance()
    {
        Vector3 P(double angle)=>new((float)(20*Math.Cos(angle)),0,(float)(-20*Math.Sin(angle)));
        Vector3[] points=[new(40,0,0),P(Math.PI/4),P(Math.PI/2),P(3*Math.PI/4),P(Math.PI),new(-40,0,0)];
        var road=new DirectedRoad("prefab:1:0:2",10,12,points);var ring=Ring();
        var adapted=Assert.Single(RoundaboutRouteAdapter.Apply([road],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        var cues=adapted.Roundabout!.Exits;
        Assert.Equal(new[]{1,2},cues.Select(c=>c.Number));
        Assert.Equal(Vector3.Distance(points[0],points[1])+Vector3.Distance(points[1],points[2]),cues[0].AlongMeters,3);
        Assert.True(cues[0].AlongMeters<cues[1].AlongMeters);
    }
}
