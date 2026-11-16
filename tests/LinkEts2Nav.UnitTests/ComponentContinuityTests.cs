using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class ComponentContinuityTests
{
    private static readonly RoadJoinPriority Priority=new(10,[],[],8,"test measured priority");
    private static NativeLaneCurve Curve(int id,Vector3 from,Vector3 to,int[] next,int[] previous)
        =>new(id,from,to,to-from,to-from,next,previous);
    private static DirectedRoad Passage(ulong uid,ulong from,ulong to,int input,int output,int[] chain,
        NativeLaneGeometry geometry,int entryCount=1,int exitCount=1)
    {
        var points=new List<Vector3>();var starts=new List<int>();
        foreach(int id in chain)
        {starts.Add(points.Count);var curve=geometry.Curves.Single(c=>c.Index==id);points.AddRange(Enumerable.Range(0,17).Select(i=>curve.Position(i/16f)));}
        var path=new PrefabLanePath(uid,from,to,input,output,input,output,entryCount,exitCount,chain,points,new LanePolyline(points).Length)
            {NativeGeometry=geometry,CurveStartPointIndices=starts};
        return new($"prefab:{uid:X16}:{from}:{to}",from,to,points)
            {LanePaths=[path],JoinPriority=Priority,NativeEntryTangent=geometry.Curves.First(c=>c.Index==chain[0]).StartTangent,
                NativeExitTangent=geometry.Curves.First(c=>c.Index==chain[^1]).EndTangent};
    }
    private static DirectedRoad[] Join(bool hard,double nextRoad=600)
    {
        if(hard)
        {
            var geometry=new NativeLaneGeometry(1,[Curve(0,new(0,0,0),new(90,0,0),[2],[]),
                Curve(1,new(0,0,-30),new(90,0,0),[2],[]),Curve(2,new(90,0,0),new(100,0,0),[],[0,1])]);
            return [Passage(1,10,30,0,0,[0,2],geometry),Passage(1,20,30,0,0,[1,2],geometry)];
        }
        var independent=new NativeLaneGeometry(1,[Curve(0,new(0,0,0),new(100,0,0),[],[]),
            Curve(1,new(0,0,-4),new(100,0,-4),[],[])]);
        return [Passage(1,10,30,0,0,[0],independent,1,2),Passage(1,20,30,0,1,[1],independent,1,2),
            new("road",30,40,[new(100,0,0),new((float)(100+nextRoad),0,0)])];
    }
    private static DirectedRoad[] Soft(double roadLength=50)
    {
        var first=Join(false,roadLength);float x=(float)(100+roadLength);
        var geometry=new NativeLaneGeometry(2,[Curve(0,new(x,0,0),new(x+100,0,0),[2],[]),
            Curve(1,new(x,0,-4),new(x+100,0,0),[2],[]),Curve(2,new(x+100,0,0),new(x+110,0,0),[],[0,1])]);
        return first.Concat([Passage(2,40,50,0,0,[0,2],geometry,2,1),Passage(2,40,50,1,0,[1,2],geometry,2,1)]).ToArray();
    }

    [Fact]
    public void ARealSharedLaneDeathInsideTheJoiningPrefabIsHard()
    {
        var edges=Join(true);var index=ComponentContinuityIndex.Build(new(edges));
        var side=index.ForEntry(edges[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Hard,side.Kind);
        Assert.Equal(90,side.ActionPoint!.Position.X,3);Assert.Equal(100,side.LifecycleEnd!.Position.X,3);
        Assert.Equal(NavigationSide.Left,side.Turn!.Side);
        Assert.True(index.ForEntry(edges[0].Id)!.IsMain);
    }
    [Fact]
    public void IndependentLanesThatContinueBeyondTheWindowAreNatural()
    {
        var edges=Join(false);var side=ComponentContinuityIndex.Build(new(edges)).ForEntry(edges[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Natural,side.Kind);
        Assert.Equal(50,side.ActionPoint!.Position.X,3);Assert.Equal(100,side.LifecycleEnd!.Position.X,3);
    }
    [Fact]
    public void ADownstreamDeathCreatesASoftEntranceAtMostTwoHundredMetresBeforeItsEnd()
    {
        var edges=Soft();var side=ComponentContinuityIndex.Build(new(edges)).ForEntry(edges[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,side.Kind);
        Assert.Equal(100,side.ActionPoint!.Position.X,3);
        Assert.Equal(250,side.IntervalEnd!.Position.X,3);Assert.Equal(260,side.LifecycleEnd!.Position.X,3);
        Assert.Equal(100,side.IntervalStart!.Position.X,3);
    }
    [Fact]
    public void FiveHundredMetresIsInclusiveAndLongerIsNatural()
    {
        var inside=Soft(399); // side curve's true arc is slightly longer than100m
        var outside=Soft(401);
        Assert.Equal(ComponentContinuityKind.Soft,ComponentContinuityIndex.Build(new(inside)).ForEntry(inside[1].Id)!.Kind);
        Assert.Equal(ComponentContinuityKind.Natural,ComponentContinuityIndex.Build(new(outside)).ForEntry(outside[1].Id)!.Kind);
    }
    [Fact]
    public void ARealMissingAttachmentIsTypedAndDoesNotBecomeNatural()
    {
        var edges=Join(false).Take(2).ToArray();
        var missing=new MissingMapSectors([new(3,4)],"fixture owner");
        var side=ComponentContinuityIndex.Build(new(edges),_=>missing).ForEntry(edges[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Unknown,side.Kind);
        Assert.IsType<MissingMapSectors>(side.Issue);
    }
    [Fact]
    public void MissingNativeGeometryCannotBeReplacedByBlinkerEvidence()
    {
        var edges=Join(true).Select(e=>e with{LanePaths=e.LanePaths.Select(p=>p with{NativeGeometry=null,
            CurveBlinkers=p.CurveIndices.Select(_=>LaneBlinker.Left).ToArray()}).ToArray()}).ToArray();
        Assert.Equal(ComponentContinuityKind.Unknown,ComponentContinuityIndex.Build(new(edges)).ForEntry(edges[1].Id)!.Kind);
    }
    [Fact]
    public void OrdinaryCrossingJunctionsDoNotUseTheComponentRampRules()
    {
        var edges=Join(true).Select(e=>e with{Junction=new(2,2,1)}).ToArray();
        Assert.Empty(ComponentContinuityIndex.Build(new(edges)).Entries);
    }
    [Fact]
    public void MultipleLaneAggregationUsesTheEarliestActionAndLatestLifetime()
    {
        var edge=Join(false)[1];
        ComponentBoundary Point(double n)=>new(edge.Id,n,new((float)n,0,0));
        ComponentLaneOutcome Lane(int order,double terminal,double action,double end)=>new(order,order,ComponentLaneFate.Dies,false,
            terminal,Point(50),Point(100),Point(terminal+100),Point(end+100),Point(action+100))
            {SoftActionDistanceFromExitMeters=action,LifecycleDistanceFromExitMeters=end};
        var result=ComponentContinuity.Aggregate(edge,false,[Lane(0,100,0,120),Lane(1,350,150,400)],1);
        Assert.Equal(ComponentContinuityKind.Soft,result.Kind);
        Assert.Equal(100,result.ActionPoint!.Position.X);Assert.Equal(500,result.LifecycleEnd!.Position.X);
    }
    [Fact]
    public void AComponentWithNoProvenThirtyPointWinnerIsUnknown()
    {
        var edges=Join(true).Select(e=>e with{JoinPriority=new(null,[],[],0,"tie")}).ToArray();
        Assert.All(ComponentContinuityIndex.Build(new(edges)).Entries,f=>Assert.Equal(ComponentContinuityKind.Unknown,f.Kind));
    }

    [Fact]
    public void SeparationEndsAtTheSeparatingPrefabEntranceRatherThanItsExit()
    {
        var first=Join(false,50);
        var geometry=new NativeLaneGeometry(2,[Curve(0,new(150,0,0),new(250,0,0),[],[]),
            Curve(1,new(150,0,-4),new(250,0,-20),[],[])]);
        var graph=first.Concat([Passage(2,40,50,0,0,[0],geometry,2,1),Passage(2,40,60,1,0,[1],geometry,2,1)]).ToArray();
        var side=ComponentContinuityIndex.Build(new(graph)).ForEntry(first[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,side.Kind);
        Assert.Equal(ComponentLaneFate.Separates,side.Lanes.Single().Fate);
        Assert.Equal(150,side.LifecycleEnd!.Position.X,3);
        Assert.Equal(100,side.ActionPoint!.Position.X,3);
        Assert.Equal(NavigationSide.Right,side.Turn!.Side);
    }

    [Fact]
    public void AllLanesSeparatingInTheInitialComponentIsSingular()
    {
        var geometry=new NativeLaneGeometry(1,[Curve(0,new(0,0,0),new(50,0,0),[1,2],[]),
            Curve(1,new(50,0,0),new(100,0,0),[],[0]),Curve(2,new(50,0,0),new(100,0,30),[],[0]),
            Curve(3,new(0,0,-4),new(100,0,34),[],[])]);
        var main=Passage(1,10,31,0,0,[0,1],geometry);
        var branch=Passage(1,10,30,0,0,[0,2],geometry,1,2);
        var side=Passage(1,20,30,0,1,[3],geometry,1,2);
        var result=ComponentContinuityIndex.Build(new([main,branch,side])).ForEntry(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Singular,result.Kind);Assert.NotNull(result.Issue);
        Assert.Null(result.ActionPoint);
    }

    [Fact]
    public void NaturalSideOutletAlsoMakesTheMainOutletRequireLaneSelectionFacts()
    {
        var geometry=new NativeLaneGeometry(1,[Curve(0,new(0,0,0),new(100,0,0),[],[]),
            Curve(1,new(0,0,4),new(100,0,20),[],[])]);
        var main=Passage(1,10,20,0,0,[0],geometry,2,1) with{SplitPriority=new(20,[],[],9,"main outlet")};
        var side=Passage(1,10,30,1,0,[1],geometry,2,1) with{SplitPriority=main.SplitPriority};
        var road=new DirectedRoad("before",1,10,[new(-600,0,0),Vector3.Zero]);
        var index=ComponentContinuityIndex.Build(new([road,main,side]));
        var selected=index.ForExit(side.Id)!;var continuation=index.ForExit(main.Id)!;
        Assert.Equal(ComponentContinuityKind.Natural,selected.Kind);
        Assert.True(continuation.IsMain);Assert.True(continuation.HasNaturalAlternative);
        Assert.NotNull(continuation.ActionPoint);Assert.NotNull(continuation.LifecycleEnd);
        Assert.Equal(100,selected.LifecycleEnd!.Position.X,3);
        Assert.Equal(2,selected.LegalExitCount);
        Assert.Equal([1],selected.EntryOrders);
    }

    [Fact]
    public void ExistingDeathAndLaterDeathCombineAsSoftRatherThanNatural()
    {
        var edge=Join(false)[1];ComponentBoundary P(float x)=>new(edge.Id,x,new(x,0,0));
        var early=new ComponentLaneOutcome(0,0,ComponentLaneFate.Dies,true,-10,P(50),P(100),P(90),P(100),P(100))
            {LifecycleDistanceFromExitMeters=0,SoftActionDistanceFromExitMeters=0};
        var late=new ComponentLaneOutcome(1,1,ComponentLaneFate.Dies,false,300,P(50),P(100),P(400),P(420),P(200))
            {LifecycleDistanceFromExitMeters=320,SoftActionDistanceFromExitMeters=100};
        var result=ComponentContinuity.Aggregate(edge,false,[early,late],1);
        Assert.Equal(ComponentContinuityKind.Soft,result.Kind);Assert.Equal(90,result.ActionPoint!.Position.X);
        Assert.Equal(420,result.LifecycleEnd!.Position.X);
    }

    [Fact]
    public void SoftOutletMirrorsBirthButAdvancesItsActionInActualTravelDirection()
    {
        var birth=new NativeLaneGeometry(1,[Curve(0,Vector3.Zero,new(20,0,0),[1,2],[]),
            Curve(1,new(20,0,0),new(100,0,0),[],[0]),Curve(2,new(20,0,0),new(100,0,4),[],[0])]);
        var keep=Passage(1,10,30,0,0,[0,1],birth,1,2);
        var added=Passage(1,10,30,0,1,[0,2],birth,1,2);
        var before=keep with{LanePaths=[keep.LanePaths[0],added.LanePaths[0]]};
        var road=new DirectedRoad("parallel",30,40,[new(100,0,0),new(350,0,0)]);
        var end=new NativeLaneGeometry(2,[Curve(0,new(350,0,0),new(450,0,0),[],[]),
            Curve(1,new(350,0,4),new(450,0,30),[],[])]);
        var main=Passage(2,40,50,0,0,[0],end,2,1) with{SplitPriority=new(50,[],[],8,"main outlet")};
        var side=Passage(2,40,60,1,0,[1],end,2,1) with{SplitPriority=main.SplitPriority};
        var result=ComponentContinuityIndex.Build(new([before,road,main,side])).ForExit(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,result.Kind);
        Assert.Equal(20,result.IntervalStart!.Position.X,3);
        Assert.Equal(350,result.IntervalEnd!.Position.X,3);
        Assert.Equal(150,result.ActionPoint!.Position.X,3);
        Assert.Equal(450,result.LifecycleEnd!.Position.X,3);
        Assert.Equal(NavigationSide.Right,result.Turn!.Side);
    }

    [Fact]
    public void SeparationAtExactlyFiveHundredMetresIsStillInsideTheWindow()
    {
        var first=Join(false,500);
        var geometry=new NativeLaneGeometry(2,[Curve(0,new(600,0,0),new(700,0,0),[],[]),
            Curve(1,new(600,0,-4),new(700,0,-20),[],[])]);
        var graph=first.Concat([Passage(2,40,50,0,0,[0],geometry,2,1),Passage(2,40,60,1,0,[1],geometry,2,1)]).ToArray();
        var side=ComponentContinuityIndex.Build(new(graph)).ForEntry(first[1].Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,side.Kind);
        Assert.Equal(600,side.LifecycleEnd!.Position.X,3);
        Assert.Equal(400,side.ActionPoint!.Position.X,3);
    }

    [Fact]
    public void EarliestLaneOwnsTheTurnWordingWhileLifetimeStillCoversEveryLane()
    {
        var edge=Join(false)[1];ComponentBoundary P(float x)=>new(edge.Id,x,new(x,0,0));
        ComponentLaneOutcome Lane(int order,double terminal,ComponentTurnKind kind)=>new(order,order,ComponentLaneFate.Dies,true,
            terminal,P(50),P(100),P((float)(100+terminal)),P(100),P(100),new(NavigationSide.Left,kind))
            {LifecycleDistanceFromExitMeters=0};
        var a=Lane(0,-20,ComponentTurnKind.Forward);var b=Lane(1,-10,ComponentTurnKind.Turn);
        var result=ComponentContinuity.Aggregate(edge,false,[a,b],1);
        Assert.Equal(80,result.ActionPoint!.Position.X);Assert.Equal(ComponentTurnKind.Forward,result.Turn!.Kind);
        Assert.Null(result.Issue);
        var conflicting=ComponentContinuity.Aggregate(edge,false,[a,b with{Turn=new(NavigationSide.Right,ComponentTurnKind.Turn)}],1);
        Assert.Null(conflicting.Turn);Assert.IsType<MapQueryFailure>(conflicting.Issue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearStraightSBranchUsesItsActualSplitSideRatherThanMirroringTheReverseMerge(bool soft)
    {
        var geometry=new NativeLaneGeometry(1,[Curve(0,Vector3.Zero,new(20,0,0),[1,2],[]),
            Curve(1,new(20,0,0),new(100,0,0),[],[0]),
            new(2,new(20,0,0),new(100,0,3),new(80,0,0),new(80,0,0),[],[0])]);
        var main=Passage(1,10,20,0,0,[0,1],geometry);
        var side=Passage(1,10,30,0,0,[0,2],geometry);
        DirectedRoad[] edges;
        if(!soft)
        {
            main=main with{SplitPriority=new(20,[],[],8,"main outlet")};side=side with{SplitPriority=main.SplitPriority};
            edges=[main,side];
        }
        else
        {
            main=Passage(1,10,30,0,0,[0,1],geometry,1,2);
            side=Passage(1,10,30,0,1,[0,2],geometry,1,2);
            var birth=main with{LanePaths=[main.LanePaths[0],side.LanePaths[0]]};
            var road=new DirectedRoad("parallel",30,40,[new(100,0,0),new(350,0,0)]);
            var end=new NativeLaneGeometry(2,[Curve(0,new(350,0,0),new(450,0,0),[],[]),Curve(1,new(350,0,3),new(450,0,30),[],[])]);
            main=Passage(2,40,50,0,0,[0],end,2,1) with{SplitPriority=new(50,[],[],8,"main outlet")};
            side=Passage(2,40,60,1,0,[1],end,2,1) with{SplitPriority=main.SplitPriority};
            edges=[birth,road,main,side];
        }
        var actual=LaneContinuity.Analyze(geometry).Points.Single(p=>p.Kind==LaneContinuityPointKind.Split)
            .Branches.Single(b=>b.CurveIndex==2);
        Assert.Equal(LaneTurnKind.ForwardRight,actual.Turn);
        var result=ComponentContinuityIndex.Build(new(edges)).ForExit(side.Id)!;
        Assert.Equal(soft?ComponentContinuityKind.Soft:ComponentContinuityKind.Hard,result.Kind);
        Assert.Equal(NavigationSide.Right,result.Turn!.Side);
        var lane=Assert.Single(result.Lanes);Assert.Equal(NavigationSide.Right,lane.Turn!.Side);
        Assert.Equal(side.LanePaths[0].EntryOrder,lane.EntryOrder);Assert.Equal(side.LanePaths[0].ExitOrder,lane.ExitOrder);
        Assert.Equal(ComponentLaneFate.Born,lane.Fate);
        Assert.Equal(side.StartNodeUid,lane.InitialMidpoint.StartNodeUid);Assert.Equal(side.EndNodeUid,lane.InitialMidpoint.EndNodeUid);
    }

    [Fact]
    public void ThirtyPointMainEntranceIdentityDoesNotResurrectLocallyDeadLanes()
    {
        var geometry=new NativeLaneGeometry(1,[
            new(0,new(0,0,-3),new(90,0,0),new(90,0,9),new(90,0,9),[2],[]),
            new(1,new(0,0,3),new(90,0,0),new(90,0,-9),new(90,0,-9),[2],[]),
            Curve(2,new(90,0,0),new(100,0,0),[],[0,1])]);
        var main=Passage(1,10,30,0,0,[0,2],geometry);var side=Passage(1,20,30,0,0,[1,2],geometry);
        Assert.All(LaneContinuity.Analyze(geometry).Points.Single().Branches,b=>Assert.Equal(LaneContinuityRole.Dead,b.Role));
        var index=ComponentContinuityIndex.Build(new([main,side]));var fact=index.ForEntry(main.Id)!;
        Assert.True(fact.IsMain);Assert.Equal(ComponentContinuityKind.Hard,fact.Kind);
        Assert.NotNull(fact.ActionPoint);Assert.NotNull(fact.LifecycleEnd);
        Assert.Equal(ComponentLaneFate.Dies,fact.Lanes.Single().Fate);
        Assert.Equal(ComponentContinuityKind.Hard,index.ForEntry(side.Id)!.Kind);
    }

    [Fact]
    public void DifferentLocalMergeOriginsInsideOnePrefabKeepDifferentConstraintIdentities()
    {
        var geometry=new NativeLaneGeometry(1,[Curve(0,Vector3.Zero,new(70,0,0),[4],[]),
            Curve(1,new(0,0,4),new(90,0,4),[5],[]),Curve(2,new(0,0,-20),new(70,0,0),[4],[]),
            Curve(3,new(0,0,-16),new(90,0,4),[5],[]),Curve(4,new(70,0,0),new(100,0,0),[],[0,2]),
            Curve(5,new(90,0,4),new(100,0,4),[],[1,3])]);
        var main=Passage(1,10,30,0,0,[0,4],geometry,2,2);
        var main2=Passage(1,10,30,1,1,[1,5],geometry,2,2);
        var side=Passage(1,20,30,0,0,[2,4],geometry,2,2);
        var side2=Passage(1,20,30,1,1,[3,5],geometry,2,2);
        main=main with{LanePaths=[main.LanePaths[0],main2.LanePaths[0]]};
        side=side with{LanePaths=[side.LanePaths[0],side2.LanePaths[0]]};
        var fact=ComponentContinuityIndex.Build(new([main,side])).ForEntry(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Hard,fact.Kind);Assert.Equal(2,fact.Lanes.Count);
        Assert.Equal(70,fact.ActionPoint!.Position.X,3);Assert.Equal(100,fact.LifecycleEnd!.Position.X,3);
        Assert.Equal(2,fact.Lanes.Select(l=>l.StructuralConstraintId).Distinct().Count());
        Assert.Contains(":4",fact.StructuralConstraintId!);Assert.Contains(":5",fact.StructuralConstraintId!);
    }
}
