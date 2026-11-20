using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using Xunit;

public sealed class ComponentMainPassageAssistTests
{
    private sealed record Scene(DirectedRoad Main,ComponentPassageContinuity MainFact,ComponentPassageContinuity SideFact,NavigationManeuver Straight);
    private static Scene Fixture()
    {
        NativeLaneCurve C(int id,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(id,a,b,b-a,b-a,next,previous);
        var native=new NativeLaneGeometry(42,[C(0,Vector3.Zero,new(0,0,50),[1,2],[]),
            C(1,new(0,0,50),new(0,0,100),[],[0]),C(2,new(0,0,50),new(-50,0,100),[],[0])])
            {InputCurveIndices=[0],OutputCurveIndices=[1,2]};
        DirectedRoad Edge(int last,ulong output)
        {
            var points=new List<Vector3>();var starts=new List<int>();
            foreach(int id in new[]{0,last})
            {starts.Add(points.Count);points.AddRange(Enumerable.Range(0,17).Select(i=>native.Curves[id].Position(i/16f)));}
            var lane=new PrefabLanePath(42,10,output,0,0,0,0,1,1,[0,last],points,new LanePolyline(points).Length)
                {NativeGeometry=native,CurveStartPointIndices=starts};
            return new($"prefab:000000000000002A:10:{output}",10,output,points)
                {LanePaths=[lane],SplitPriority=new(20,[],[],8,"cached main outlet")};
        }
        var main=Edge(1,20);var side=Edge(2,30);DirectedRoad[] graph=[main,side];
        var index=ComponentContinuityIndex.Build(new(graph));
        var mainFact=index.ForExit(main.Id)!;var sideFact=index.ForExit(side.Id)!;
        Assert.Equal(ComponentContinuityKind.Main,mainFact.Kind);
        Assert.Equal(ComponentContinuityKind.Hard,sideFact.Kind);
        Assert.Equal(NavigationSide.Right,sideFact.Turn!.Side);
        var projected=ComponentManeuverProjection.Build([main],0,[],[mainFact,sideFact],main.LanePaths[0]);
        var straight=Assert.Single(projected.Actions);
        Assert.Equal(ManeuverKind.Straight,straight.Kind);
        return new(main,mainFact,sideFact,straight);
    }
    private static GuidanceSourcePlan Plan(NavigationManeuver straight,ManeuverKind targetKind=ManeuverKind.TurnRight)
    {
        var target=new NavigationManeuver("target",targetKind,150,"in","out")
            {JunctionId="target",JunctionKind=NavigationJunctionKind.Ordinary,EndDistanceMeters=180};
        return new NavigationEventPlanner().Build("session","route",DateTimeOffset.Parse("2026-11-16T00:00:00Z"),
            0,[straight,target],true,false,false,new());
    }
    [Fact]
    public void MainPassageMustCarryTheExistingRightHandNativeOutlet()
    {
        var straight=Fixture().Straight;
        Assert.Equal(NavigationSide.Right,straight.StraightExitSides);
    }
    [Fact]
    public void ComponentRightOutletMustAllowTheSameSidePassageReminder()
    {
        var straight=Fixture().Straight;
        var control=Plan(straight with{StraightExitSides=NavigationSide.Right});
        Assert.Contains(control.Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
        var actual=Plan(straight);
        Assert.Contains(actual.Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
    }
    [Fact]
    public void AnOppositeSideTargetStillHasNoPassageReminder()
        =>Assert.DoesNotContain(Plan(Fixture().Straight,ManeuverKind.TurnLeft).Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
    [Fact]
    public void AnUnknownOutletDoesNotBecomeAnInventedLegalSide()
    {
        var scene=Fixture();var unknown=scene.SideFact with{Kind=ComponentContinuityKind.Unknown};
        Assert.Throws<NotSupportedException>(()=>ComponentManeuverProjection.Build([scene.Main],0,[],[scene.MainFact,unknown],scene.Main.LanePaths[0]));
    }
    [Fact]
    public void AnEntryMainUsesTheSameOutletSideFacts()
    {
        var scene=Fixture();
        var straight=Assert.Single(ComponentManeuverProjection.Build([scene.Main],0,[scene.MainFact],
            [scene.MainFact,scene.SideFact],scene.Main.LanePaths[0]).Actions);
        Assert.Equal(NavigationSide.Right,straight.StraightExitSides);
    }
    [Fact]
    public void AnOutletWithoutATurnDoesNotInventPassageSideFromItsPosition()
    {
        var scene=Fixture();
        // Projection-only contract: the producer's role is not reconstructed
        // here, and a missing turn is not a statement about game reachability.
        var other=scene.SideFact with{Turn=null};
        Assert.Throws<NotSupportedException>(()=>ComponentManeuverProjection.Build([scene.Main],0,[],[scene.MainFact,other],scene.Main.LanePaths[0]));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturalOutletsUseTheirLaneSetsWithoutATurnOrPassageFallback(bool selectedIsMain)
    {
        var scene=Fixture();
        // Two disjoint input lane groups lead to the two outlets. Unlike the
        // old all-lanes fixture, neither outlet represents the entire input.
        // These are projection inputs, not evidence of an actual game prefab.
        var selected=scene.MainFact with{Kind=selectedIsMain?ComponentContinuityKind.Main:ComponentContinuityKind.Natural,
            IsMain=selectedIsMain,HasNaturalAlternative=selectedIsMain,EntryLaneCount=2,EntryOrders=[0],Turn=null};
        var other=scene.SideFact with{Kind=ComponentContinuityKind.Natural,IsMain=false,EntryLaneCount=2,EntryOrders=[1],Turn=null};
        var result=ComponentManeuverProjection.Build([scene.Main],0,[],[selected,other]);
        var choice=Assert.Single(result.Actions);
        Assert.Equal(ManeuverKind.ChooseLanes,choice.Kind);
        Assert.Equal("分岔口靠左，走左侧车道",choice.Instruction);
        Assert.Empty(result.Issues);Assert.Empty(result.Unresolved);
    }
}
