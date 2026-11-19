using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;
public sealed class RoundaboutNavigationTests
{
    private static IReadOnlyList<RoundaboutRouteRange> Build(IReadOnlyList<DirectedRoad> route,IReadOnlyDictionary<ulong,RoundaboutStructure> annotations)
        =>RoundaboutRouteAdapter.Build(route,annotations,Enumerable.Range(0,route.Count).ToArray());
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
        var ranges=Build(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring}});
        Assert.Equal(ordinal,Assert.Single(ranges).Actions[^1].SpeechSequenceOrder);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PolarOrderingUsesSeparatePhysicalEntryAndExitEndpoints(bool mirrored)
    {
        var ring=Ring(mirrored);
        var road=new DirectedRoad("prefab:1:0:3",10,13,[Vector3.Zero,new(100,0,0)]);
        var range=Assert.Single(Build([road],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        Assert.Equal(3,range.Actions[^1].SpeechSequenceOrder);
        Assert.Equal(new double[]{1,2,3},range.Actions.Skip(1).Select(e=>e.SpeechSequenceOrder));
        Assert.NotEqual(ring.EntryPorts.Single(p=>p.NodeUid==10).Bearing,
            ring.ExitPorts.Single(p=>p.BoundaryNodeUid==10).Bearing);
    }

    [Fact]
    public void CompositePassagePreservesActualGeometryAndUsesOnePhysicalDecision()
    {
        var ring=Ring() with{PrefabUids=[1,2]};
        DirectedRoad[] route=[new("prefab:1:0:1",10,50,[new(40,0,0),new(20,0,0),new(0,0,-20)]),
            new("prefab:2:0:1",50,12,[new(0,0,-20),new(-20,0,0),new(-40,0,0)])];
        var originals=route.ToArray();var points=route.SelectMany(e=>e.Points).ToArray();
        var ranges=Build(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring},{2,ring}});
        var range=Assert.Single(ranges);Assert.Equal(2,range.Actions[^1].SpeechSequenceOrder);
        Assert.Equal(0,range.StartVisitIndex);Assert.Equal(2,range.EndVisitIndexExclusive);
        Assert.All(Enumerable.Range(0,route.Length),i=>Assert.Same(originals[i],route[i]));
        Assert.Equal(points,route.SelectMany(e=>e.Points));
        var maneuvers=Detect(route,ranges);
        Assert.Equal(new[]{ManeuverKind.EnterRoundabout,ManeuverKind.PassRoundaboutExit,ManeuverKind.ExitRoundabout},maneuvers.Select(m=>m.Kind));
        double length=route.Sum(RouteGeometry.Length);
        Assert.All(maneuvers, m => Assert.Equal(length, m.EndDistanceMeters));
        Assert.Equal("进入环岛",maneuvers[0].Instruction);
        Assert.Equal("进入环岛，请从第二出口驶出",maneuvers[0].NearInstruction);
        Assert.Equal(length-20,maneuvers[^1].DistanceMeters,3);
        Assert.Equal(20,range.EndMeters-maneuvers[^1].DistanceMeters,3);
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
        var range=Assert.Single(Build([selected],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        Assert.Equal(80,range.Actions[^1].DistanceMeters);
        Assert.Equal(20,range.EndMeters-range.Actions[^1].DistanceMeters);
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
        Assert.Throws<NotSupportedException>(()=>Build([edge],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void EmptyCandidatesOrDegeneratePathCannotBecomeAProjectedZeroDeparture(bool emptyCandidates)
    {
        var ring=Ring();
        if(emptyCandidates)ring=ring with{ExitPorts=ring.ExitPorts.Select(p=>p.BoundaryNodeUid==12?p with{DepartureBranches=[]}:p).ToArray()};
        var edge=new DirectedRoad("prefab:1:0:2",10,12,emptyCandidates?[Vector3.Zero,new(100,0,0)]:[Vector3.Zero,Vector3.Zero]);
        Assert.Throws<InvalidDataException>(()=>Build([edge],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
    }
    [Fact] public void IndistinguishableBearingsAreReportedInsteadOfReturningTheUnadaptedRoute()
    {
        var ring=Ring();ring=ring with{ExitPorts=ring.ExitPorts.Select(p=>p with{Bearing=0}).ToArray()};
        var edge=new DirectedRoad("prefab:1:0:2",10,12,[Vector3.Zero,new(100,0,0)]);
        Assert.Throws<NotSupportedException>(()=>Build([edge],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
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
        var edge=new DirectedRoad("ring",10,20,[Vector3.Zero,new(100,0,0)]);
        var range=FixtureRange(edge,[70]);
        var actions=Detect([edge],[range],50);
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
        var edge=new DirectedRoad("ring",10,20,[Vector3.Zero,new(200,0,0)]);
        var range=FixtureRange(edge,[50,100,150]);
        var before=Detect([edge],[range]);
        var plan=policy.PlanSequence(before,"s",now,now,true);
        Assert.Contains("经过第一出口。",plan);Assert.Contains("经过第二出口。",plan);Assert.Contains("驶出环岛。",plan);
        foreach(var (along,text) in new[]{(30,"经过第一出口。"),(80,"经过第二出口。"),(130,"驶出环岛。")})
        {
            now=now.AddSeconds(1);var actions=Detect([edge],[range],along);
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
        var range=Assert.Single(Build([road],new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        var cues=range.Actions.Skip(1).ToArray();
        Assert.Equal(new double[]{1,2},cues.Select(c=>c.SpeechSequenceOrder));
        Assert.Equal(Vector3.Distance(points[0],points[1])+Vector3.Distance(points[1],points[2]),cues[0].DistanceMeters,3);
        Assert.True(cues[0].DistanceMeters<cues[1].DistanceMeters);
    }

    [Fact]
    public void ConsecutiveVisitsOfTheSamePhysicalRingRemainSeparateRangesAndSeries()
    {
        var ring=Ring();
        var edge=new DirectedRoad("prefab:1:0:0",10,10,[new(40,0,0),new(0,0,-30),new(-30,0,0),new(0,0,30),new(40,0,0)])
            {NativeCurveSpans=[new(100,0)]};
        DirectedRoad[] route=[edge,edge];
        var ranges=Build(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring}});
        Assert.Equal(2,ranges.Count);Assert.Equal((0,1),(ranges[0].StartVisitIndex,ranges[0].EndVisitIndexExclusive));
        Assert.Equal((1,2),(ranges[1].StartVisitIndex,ranges[1].EndVisitIndexExclusive));
        Assert.Equal(ranges[0].EndMeters,ranges[1].StartMeters);
        Assert.NotEqual(ranges[0].Actions[0].SpeechSequenceId,ranges[1].Actions[0].SpeechSequenceId);
        Assert.Same(edge,route[0]);Assert.Same(edge,route[1]);
        Assert.Equal(8,Detect(route,ranges).Count(m=>m.ExecutionOnly));
    }

    [Fact]
    public void RangeActionsUseTheOriginalRouteOffsetsAndLeaveNativeSpansIntact()
    {
        var ring=Ring();var prefix=new DirectedRoad("road",1,10,[Vector3.Zero,new(123,0,0)]);
        var passage=new DirectedRoad("prefab:1:0:2",10,12,[new(123,0,0),new(203,0,0),new(223,0,0)])
            {NativeCurveSpans=[new(100,0),new(102,1)]};
        DirectedRoad[] route=[prefix,passage];var originalSpans=passage.NativeCurveSpans;
        var range=Assert.Single(Build(route,new Dictionary<ulong,RoundaboutStructure>{{1,ring}}));
        Assert.Equal((1,2),(range.StartVisitIndex,range.EndVisitIndexExclusive));
        Assert.Equal(123,range.StartMeters);Assert.Equal(223,range.EndMeters);
        Assert.Equal(123,range.Actions[0].DistanceMeters);
        Assert.All(range.Actions,a=>Assert.Equal(223,a.EndDistanceMeters));
        Assert.Same(passage,route[1]);Assert.Same(originalSpans,route[1].NativeCurveSpans);
        Assert.Equal(new[]{new PrefabCurveSpan(100,0),new PrefabCurveSpan(102,1)},route[1].NativeCurveSpans);
    }

    [Fact]
    public void MarkedReturnPassageDoesNotAlsoGenerateAnOrdinaryUTurn()
    {
        var ring=Ring();
        var edge=new DirectedRoad("prefab:1:0:0",10,10,[new(40,0,0),new(-40,0,0),new(40,0,0)])
            {LanePaths=[new(1,10,10,0,0,0,0,1,1,[0],[new(40,0,0),new(-40,0,0),new(40,0,0)],160)]};
        var ranges=Build([edge],new Dictionary<ulong,RoundaboutStructure>{{1,ring}});
        var actions=Detect([edge],ranges);
        Assert.DoesNotContain(actions,a=>a.Kind==ManeuverKind.UTurn);
        Assert.Single(actions,a=>a.Kind==ManeuverKind.EnterRoundabout);
        Assert.Single(actions,a=>a.Kind==ManeuverKind.ExitRoundabout);
    }

    [Fact]
    public void MarkedRangeIsExcludedBeforeOrdinaryComponentSelectionIsRequired()
    {
        NativeLaneCurve C(int i,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(i,a,b,b-a,b-a,next,previous);
        var geometry=new NativeLaneGeometry(42,
            [C(0,Vector3.Zero,new(90,0,0),[2],[]),C(1,new(0,0,30),new(90,0,0),[2],[]),C(2,new(90,0,0),new(100,0,0),[],[0,1])])
            {InputCurveIndices=[0,1],OutputCurveIndices=[2]};
        DirectedRoad Passage(int branch,ulong start)
        {
            Vector3[] points=[geometry.Curves[branch].Start,new(90,0,0),new(100,0,0)];
            var lane=new PrefabLanePath(42,start,30,0,0,0,0,1,1,[branch,2],points,RouteGeometry.Length(new("measure",start,30,points)))
                {NativeGeometry=geometry,CurveStartPointIndices=[0,1]};
            return new($"prefab:2A:{start}:30",start,30,points)
                {LanePaths=[lane],JoinPriority=new(10,[],[],8,"fixture main entrance")};
        }
        var main=Passage(0,10);var selected=Passage(1,20);
        var detector=new NavigationManeuverDetector();var range=FixtureRange(selected,[70]);
        var actions=detector.FindSequence([selected],0,[main,selected],out _,out _,out _,roundaboutRanges:[range]);
        Assert.Equal(new[]{ManeuverKind.EnterRoundabout,ManeuverKind.ExitRoundabout},actions.Select(a=>a.Kind));
        Assert.Empty(detector.Components.Unresolved);Assert.Empty(detector.Components.Issues);
    }

    internal static IReadOnlyList<NavigationManeuver> Detect(IReadOnlyList<DirectedRoad> route,
        IReadOnlyList<RoundaboutRouteRange> ranges,double player=0)
        =>new NavigationManeuverDetector().FindSequence(route,player,[],out _,out _,out _,roundaboutRanges:ranges);

    // These fixtures exercise already-marked action consumption, independently
    // of physical roundabout discovery and the geometry-based range builder.
    internal static RoundaboutRouteRange FixtureRange(DirectedRoad road,double[] exits)
    {
        double end=RouteGeometry.Length(road);const string series="ring:fixture";
        var actions=new List<NavigationManeuver>
        {
            new("entry",ManeuverKind.EnterRoundabout,0,road.Id,road.Id)
                {EndDistanceMeters=end,JunctionId="roundabout:ring",SpeechSequenceId=series,SpeechSequenceStartDistanceMeters=0}
        };
        actions.AddRange(exits.Select((along,i)=>new NavigationManeuver("exit:"+i,
            i==exits.Length-1?ManeuverKind.ExitRoundabout:ManeuverKind.PassRoundaboutExit,along,road.Id,road.Id)
            {EndDistanceMeters=end,JunctionId="roundabout:ring",SpeechSequenceId=series,SpeechSequenceStartDistanceMeters=0,
                ExecutionOnly=true,SpeechSequenceOrder=i+1,
                Instruction=i==exits.Length-1?"驶出环岛":"经过第"+NavigationPrompts.Number(i+1)+"出口"}));
        return new("ring",0,1,0,end,actions);
    }
}
