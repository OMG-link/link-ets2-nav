using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class ComponentReferenceRangeTests
{
    private static NativeLaneCurve Curve(int index,Vector3 start,Vector3 end,int[] next,int[] previous)
        =>new(index,start,end,end-start,end-start,next,previous);
    private static DirectedRoad Passage(ulong prefab,ulong from,ulong to,int entry,int exit,int[] chain,
        NativeLaneGeometry geometry,int inputs=2,int outputs=2,RoadJoinPriority? priority=null)
    {
        var points=new List<Vector3>();var starts=new List<int>();
        foreach(int id in chain){starts.Add(points.Count);var c=geometry.Curves.Single(c=>c.Index==id);points.AddRange(Enumerable.Range(0,65).Select(i=>c.Position(i/64f)));}
        var path=new PrefabLanePath(prefab,from,to,entry,exit,entry,exit,inputs,outputs,chain,points,new LanePolyline(points).Length)
            {NativeGeometry=geometry,CurveStartPointIndices=starts};
        return new($"prefab:{prefab:X16}:{from}:{to}",from,to,points){LanePaths=[path],JoinPriority=priority??new(10,[],[],8,"fixture known main")};
    }
    private static (DirectedRoad[] Graph,DirectedRoad Side) Initial(bool twoMain=false)
    {
        var geometry=NativeLaneFixture.WithInterfaces(1,[Curve(0,Vector3.Zero,new(100,0,0),[],[]),
            Curve(1,new(0,0,4),new(100,0,4),[],[]),Curve(2,new(0,0,8),new(100,0,8),[],[])]);
        int count=twoMain?3:2;
        var main=Passage(1,10,30,0,0,[0],geometry,twoMain?2:1,count);
        if(twoMain)main=main with{LanePaths=[main.LanePaths[0],Passage(1,10,30,1,1,[1],geometry,2,count).LanePaths[0]]};
        int order=twoMain?2:1;
        var side=Passage(1,20,30,0,order,[order],geometry,1,count);
        var road=new DirectedRoad("road:parallel",30,40,[new(100,0,0),new(150,0,0)]);
        return([main,side,road],side);
    }
    private static DirectedRoad[] Ending(bool bothDie=false)
    {
        var a=bothDie?new Vector3(150,0,-3):new Vector3(150,0,0);
        var b=bothDie?new Vector3(150,0,3):new Vector3(150,0,4);
        var origin=new Vector3(250,0,0);var end=new Vector3(260,0,0);
        var first=new NativeLaneCurve(0,a,origin,new(100,0,bothDie?9:60),new(100,0,bothDie?9:60),[2],[]);
        var second=bothDie?new NativeLaneCurve(1,b,origin,new(196,0,-9),new(4,0,-9),[2],[]):Curve(1,b,origin,[2],[]);
        var geometry=NativeLaneFixture.WithInterfaces(2,[first,second,Curve(2,origin,end,[],[0,1])]);
        var point=Assert.Single(LaneContinuity.Analyze(geometry).Points);
        Assert.Equal(LaneContinuityRole.Dead,point.Branches.Single(b=>b.CurveIndex==0).Role);
        Assert.Equal(bothDie?LaneContinuityRole.Dead:LaneContinuityRole.Survivor,point.Branches.Single(b=>b.CurveIndex==1).Role);
        var edge=Passage(2,40,50,0,0,[0,2],geometry,2,1);
        return [edge with{LanePaths=[edge.LanePaths[0],Passage(2,40,50,1,0,[1,2],geometry,2,1).LanePaths[0]]}];
    }
    [Fact]
    public void ReferenceDeathEndsObservationAtItsNativeOriginRatherThanComponentEntrance()
    {
        var initial=Initial();var ending=Ending();var graph=initial.Graph.Concat(ending).ToArray();
        var side=ComponentContinuityIndex.Build(new(graph)).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Natural,side.Kind);Assert.Null(side.Issue);
        var lane=Assert.Single(side.Lanes);
        double local=new LanePolyline(ending[0].LanePaths[0].Points).Along[ending[0].LanePaths[0].CurveStartPointIndices[1]];
        Assert.Equal(50+local,lane.TerminalDistanceFromExitMeters,6);
        Assert.True(lane.TerminalDistanceFromExitMeters>149); // The interface alone was only50m ahead.
        Assert.Equal(50,side.ActionPoint!.Position.X,3);Assert.Equal(100,side.LifecycleEnd!.Position.X,3);
    }
    [Fact]
    public void SideDeathAtTheSameNativeOriginIsStillARealSoftMerge()
    {
        var initial=Initial();var ending=Ending(bothDie:true);
        var side=ComponentContinuityIndex.Build(new(initial.Graph.Concat(ending).ToArray())).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,side.Kind);Assert.Null(side.Issue);
        var lane=Assert.Single(side.Lanes);Assert.Equal(ComponentLaneFate.Dies,lane.Fate);
        double mainEnd=new LanePolyline(ending[0].LanePaths[0].Points).Along[ending[0].LanePaths[0].CurveStartPointIndices[1]];
        double sideEnd=new LanePolyline(ending[0].LanePaths[1].Points).Along[ending[0].LanePaths[1].CurveStartPointIndices[1]];
        Assert.True(sideEnd>mainEnd); // Arc inequality must not lose their proven common origin.
        Assert.Equal(new Vector3(250,0,0),lane.Terminal!.Position);
        Assert.Equal(260,side.LifecycleEnd!.Position.X,3);
    }
    [Fact]
    public void OneSurvivingReferenceKeepsObservationAliveForALaterSideDeath()
    {
        var initial=Initial(twoMain:true);
        var b=NativeLaneFixture.WithInterfaces(2,[
            new(0,new(150,0,0),new(250,0,8),new(100,0,60),new(100,0,60),[3],[]),
            Curve(1,new(150,0,4),new(260,0,4),[],[]),
            Curve(2,new(150,0,8),new(250,0,8),[3],[]),Curve(3,new(250,0,8),new(260,0,8),[],[0,2])]);
        var before=Passage(2,40,50,0,0,[0,3],b,3,2);
        before=before with{LanePaths=[before.LanePaths[0],Passage(2,40,50,1,1,[1],b,3,2).LanePaths[0],Passage(2,40,50,2,0,[2,3],b,3,2).LanePaths[0]]};
        var road=new DirectedRoad("road:after",50,60,[new(260,0,0),new(310,0,0)]);
        var c=NativeLaneFixture.WithInterfaces(3,[
            new(0,new(310,0,8),new(400,0,4),new(90,0,60),new(90,0,60),[2],[]),
            Curve(1,new(310,0,4),new(400,0,4),[2],[]),Curve(2,new(400,0,4),new(410,0,4),[],[0,1])]);
        var after=Passage(3,60,70,0,0,[0,2],c,2,1);
        after=after with{LanePaths=[after.LanePaths[0],Passage(3,60,70,1,0,[1,2],c,2,1).LanePaths[0]]};
        var side=ComponentContinuityIndex.Build(new(initial.Graph.Concat([before,road,after]).ToArray())).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Soft,side.Kind);Assert.Null(side.Issue);
        Assert.Equal(400,Assert.Single(side.Lanes).Terminal!.Position.X,3);
        Assert.Equal(410,side.LifecycleEnd!.Position.X,3);
    }
    [Fact]
    public void SameOriginDoesNotExtendTheOriginalFiveHundredMetreHardLimit()
    {
        var initial=Initial();var ending=Ending(bothDie:true)[0];
        var main=ending.LanePaths[0];double local=new LanePolyline(main.Points).Along[main.CurveStartPointIndices[1]];
        double roadLength=499.9-local;var delta=new Vector3((float)(roadLength-50),0,0);
        var geometry=main.NativeGeometry! with{Curves=main.NativeGeometry!.Curves.Select(c=>c with{Start=c.Start+delta,End=c.End+delta}).ToArray()};
        ending=ending with{Points=ending.Points.Select(p=>p+delta).ToArray(),LanePaths=ending.LanePaths.Select(p=>p with
            {Points=p.Points.Select(q=>q+delta).ToArray(),NativeGeometry=geometry}).ToArray()};
        var road=initial.Graph[2] with{Points=[new(100,0,0),new((float)(100+roadLength),0,0)]};
        double sideEnd=RouteGeometry.Length(road)+new LanePolyline(ending.LanePaths[1].Points).Along[ending.LanePaths[1].CurveStartPointIndices[1]];
        Assert.True(sideEnd>500.1);
        var result=ComponentContinuityIndex.Build(new([initial.Graph[0],initial.Side,road,ending])).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Natural,result.Kind);Assert.Null(result.Issue);
        var lane=Assert.Single(result.Lanes);Assert.Equal(ComponentLaneFate.Continues,lane.Fate);
        Assert.InRange(lane.TerminalDistanceFromExitMeters,499.89,499.91);
    }
    [Fact]
    public void ARealMissingAttachmentStillRequestsItsNativeLookupKey()
    {
        var initial=Initial();var missing=new MissingMapObjects([new(MapObjectKind.Node,40)],"unloaded next component");
        var side=ComponentContinuityIndex.Build(new(initial.Graph),_=>missing).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Unknown,side.Kind);
        Assert.Equal(missing.Keys,Assert.IsType<MissingMapObjects>(side.Issue).Keys);
    }
#if DEBUG
    [Fact]
    public void InvalidNativeReferenceFailsWhenFirstEncountered()
    {
        var initial=Initial(twoMain:true);var a=new Vector3(250,0,0);
        // Constructed partial native topology: the merge names branch4, but
        // branch4 lacks its reciprocal successor. This is producer error data,
        // not a complete valid PPD or evidence from a recorded player route.
        var b=NativeLaneFixture.WithInterfaces(2,[Curve(0,new(150,0,0),a,[3],[]),
            Curve(1,new(150,0,4),new(260,0,4),[],[]),Curve(2,new(150,0,8),new(260,0,8),[],[]),
            Curve(3,a,new(260,0,0),[],[0,4]),Curve(4,new(150,0,-4),a,[],[])]);
        Assert.Null(LaneContinuity.Analyze(b).MapIssue);
        Assert.Contains(LaneContinuity.Analyze(b).Points,p=>p.MapIssue is MapQueryFailure);
        var middle=Passage(2,40,50,0,0,[0,3],b,3,3);
        middle=middle with{LanePaths=[middle.LanePaths[0],Passage(2,40,50,1,1,[1],b,3,3).LanePaths[0],
            Passage(2,40,50,2,2,[2],b,3,3).LanePaths[0]]};
        var graph=new NativeLaneGraph(initial.Graph.Append(middle).ToArray());
        var exception=Assert.Throws<NavigationContractException>(()=>ComponentContinuityIndex.Build(graph));
        Assert.Contains(middle.Id,exception.Message);
        Assert.Contains("not reciprocal",exception.Message);
    }
#endif
    [Fact]
    public void LastConfirmedReferenceDeathDefinesTheRangeWhenAllReferencesEnd()
    {
        var initial=Initial(twoMain:true);var a=new Vector3(210,0,8);var b=new Vector3(250,0,8);
        var geometry=NativeLaneFixture.WithInterfaces(2,[
            new(0,new(150,0,0),a,new(60,0,36),new(60,0,36),[3],[]),
            new(1,new(150,0,4),b,new(100,0,60),new(100,0,60),[5],[]),
            Curve(2,new(150,0,8),a,[3],[]),Curve(3,a,b,[5],[0,2]),Curve(5,b,new(260,0,8),[],[1,3])]);
        var after=Passage(2,40,50,0,0,[0,3,5],geometry,3,1);
        after=after with{LanePaths=[after.LanePaths[0],Passage(2,40,50,1,0,[1,5],geometry,3,1).LanePaths[0],
            Passage(2,40,50,2,0,[2,3,5],geometry,3,1).LanePaths[0]]};
        double End(PrefabLanePath p)=>50+new LanePolyline(p.Points).Along[p.CurveStartPointIndices[1]];
        Assert.True(End(after.LanePaths[1])>End(after.LanePaths[0])+30);
        var side=ComponentContinuityIndex.Build(new(initial.Graph.Concat([after]).ToArray())).ForEntry(initial.Side.Id)!;
        Assert.Equal(ComponentContinuityKind.Natural,side.Kind);Assert.Null(side.Issue);
        Assert.Equal(End(after.LanePaths[1]),Assert.Single(side.Lanes).TerminalDistanceFromExitMeters,6);
    }
    [Fact]
    public void ReferenceUsesItsOwnLengthWithoutProjectingOntoAnUnrelatedParallelLane()
    {
        var initial=Initial();var first=initial.Graph[0];var geometry=first.LanePaths[0].NativeGeometry!;
        var third=Passage(1,22,30,0,2,[2],geometry,1,3);
        var b=NativeLaneFixture.WithInterfaces(2,[
            new(0,new(150,0,0),new(250,0,8),new(100,0,60),new(100,0,60),[3],[]),
            Curve(1,new(150,0,4),new(260,0,4),[],[]),
            Curve(2,new(150,0,8),new(250,0,8),[3],[]),Curve(3,new(250,0,8),new(260,0,8),[],[0,2])]);
        var after=Passage(2,40,50,0,0,[0,3],b,3,2);
        after=after with{LanePaths=[after.LanePaths[0],Passage(2,40,50,1,1,[1],b,3,2).LanePaths[0],
            Passage(2,40,50,2,0,[2,3],b,3,2).LanePaths[0]]};
        Assert.DoesNotContain(3,after.LanePaths[1].CurveIndices);
        var graph=initial.Graph.Select(e=>e with{LanePaths=e.LanePaths.Select(p=>p with{ExitLaneCount=3}).ToArray()})
            .Concat([third,after]).ToArray();
        var side=ComponentContinuityIndex.Build(new(graph)).ForEntry(initial.Side.Id)!;
        double referenceEnd=50+new LanePolyline(after.LanePaths[0].Points).Along[after.LanePaths[0].CurveStartPointIndices[1]];
        Assert.Equal(ComponentContinuityKind.Natural,side.Kind);Assert.Null(side.Issue);
        Assert.Equal(referenceEnd,Assert.Single(side.Lanes).TerminalDistanceFromExitMeters,6);
    }
    [Fact]
    public void EarlierGeometricVisitCannotReplaceTheNativeDeathVisit()
    {
        var initial=Initial();var origin=new Vector3(250,0,0);
        var b=NativeLaneFixture.WithInterfaces(2,[
            Curve(0,new(150,0,0),origin,[1],[]),Curve(1,origin,new(200,0,30),[2],[0]),
            Curve(2,new(200,0,30),origin,[4],[1]),Curve(3,new(150,0,4),origin,[4],[]),
            Curve(4,origin,new(260,0,0),[],[2,3])]);
        var after=Passage(2,40,50,0,0,[0,1,2,4],b,2,1);
        after=after with{LanePaths=[after.LanePaths[0],Passage(2,40,50,1,0,[3,4],b,2,1).LanePaths[0]]};
        var side=ComponentContinuityIndex.Build(new(initial.Graph.Concat([after]).ToArray())).ForEntry(initial.Side.Id)!;
        var path=after.LanePaths[0];var line=new LanePolyline(path.Points);
        double actual=line.Along[path.CurveStartPointIndices[3]];
        Assert.True(actual>line.Project(origin).Along+100);
        Assert.Equal(ComponentContinuityKind.Natural,side.Kind);Assert.Null(side.Issue);
        Assert.Equal(50+actual,Assert.Single(side.Lanes).TerminalDistanceFromExitMeters,6);
    }
    [Fact]
    public void NoReferenceAtTheInitialComponentDoesNotBecomeNatural()
    {
        var end=Ending()[0];
        var main=end with{Id="prefab:0000000000000002:10:50",StartNodeUid=10,LanePaths=[end.LanePaths[0] with{EntryNodeUid=10,EntryLaneCount=1}]};
        var side=end with{Id="prefab:0000000000000002:20:50",StartNodeUid=20,LanePaths=[end.LanePaths[1] with{EntryNodeUid=20,EntryOrder=0,EntryLaneCount=1}]};
        var fact=ComponentContinuityIndex.Build(new([main,side])).ForEntry(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Unknown,fact.Kind);
        Assert.Contains("主线资格已丢失",fact.Issue!.Reason);
    }
}
