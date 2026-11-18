using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class SelectedLaneTurnTests
{
    private static readonly ComponentTurn Left=new(NavigationSide.Left,ComponentTurnKind.Forward);
    private static readonly ComponentTurn Right=new(NavigationSide.Right,ComponentTurnKind.Forward);
    private static readonly DirectedRoad Edge=new("prefab:2A:1:2",1,2,[Vector3.Zero,new(0,0,100)]);
    private static ComponentBoundary At(double at)=>new(Edge.Id,at,new(0,0,(float)at)){StartNodeUid=1,EndNodeUid=2};
    private static PrefabLanePath Selected(int order=0)=>new(42,1,2,order,order,order,order,2,2,[order],Edge.Points,100);
    private static ComponentPassageContinuity Fact(ComponentContinuityKind kind,ComponentTurn? laneTurn)=>new(Edge.Id,42,1,2,false,kind,
        At(50),At(100),At(0),At(80),[0],[0],2,2,2,Left,
        [new(0,0,ComponentLaneFate.Dies,kind==ComponentContinuityKind.Hard,50,At(0),At(0),At(50),At(100),At(50),laneTurn)]);
    private static ComponentManeuverProjectionResult Project(ComponentPassageContinuity fact,PrefabLanePath? selected)
        =>ComponentManeuverProjection.Build([Edge],0,[fact],[],selected);
    [Theory][InlineData(ComponentContinuityKind.Hard)][InlineData(ComponentContinuityKind.Soft)]
    public void DirectionWithoutSelectedPathIsInvalidEvenWhenAggregateHasATurn(ComponentContinuityKind kind)
    {
        var error=Assert.Throws<ArgumentNullException>(()=>Project(Fact(kind,Right),null));
        Assert.Equal("selectedPath",error.ParamName);Assert.Contains(Edge.Id,error.Message);
    }
    [Theory][InlineData(ComponentContinuityKind.Hard)][InlineData(ComponentContinuityKind.Soft)]
    public void UnmatchedSelectedLaneCannotBorrowTheAggregateTurn(ComponentContinuityKind kind)
    {
        var error=Assert.Throws<ArgumentException>(()=>Project(Fact(kind,Right),Selected(1)));
        Assert.Equal("selectedPath",error.ParamName);Assert.Contains("1->1",error.Message);Assert.Contains(Edge.Id,error.Message);
    }
    [Theory][InlineData(ComponentContinuityKind.Hard)][InlineData(ComponentContinuityKind.Soft)]
    public void SelectedLaneWithoutATurnCannotBorrowTheAggregateTurn(ComponentContinuityKind kind)
    {
        var error=Assert.Throws<ArgumentException>(()=>Project(Fact(kind,null),Selected()));
        Assert.Contains("0->0",error.Message);Assert.Contains(Edge.Id,error.Message);
    }
    [Theory][InlineData(ComponentContinuityKind.Hard)][InlineData(ComponentContinuityKind.Soft)]
    public void ActualLaneDirectionOverridesContradictoryAggregateWithoutChangingAnchors(ComponentContinuityKind kind)
    {
        var action=Assert.Single(Project(Fact(kind,Right),Selected()).Actions);
        Assert.Equal(ManeuverKind.KeepRight,action.Kind);Assert.Equal(50,action.DistanceMeters);Assert.Equal(100,action.EndDistanceMeters);
        if(kind==ComponentContinuityKind.Hard)Assert.Contains("\u5de6",action.ExecutionInstruction);
        else Assert.Equal(new LaneActionInterval(0,80),action.ApproachInterval);
    }
    [Theory][InlineData(ComponentContinuityKind.Natural,false)][InlineData(ComponentContinuityKind.Main,true)]
    public void LaneSelectionDoesNotConsumeATurn(ComponentContinuityKind kind,bool naturalAlternative)
    {
        var fact=Fact(kind,null) with{HasNaturalAlternative=naturalAlternative};
        var result=ComponentManeuverProjection.Build([Edge with{LanePaths=[Selected()]}],0,[],[fact]);
        Assert.Equal(ManeuverKind.ChooseLanes,Assert.Single(result.Actions).Kind);Assert.Empty(result.Issues);
    }
    [Fact]
    public void TransparentMainPassageDoesNotConsumeATurn()
    {
        var fact=Fact(ComponentContinuityKind.Main,null) with{Lanes=[],IsMain=true};
        var result=Project(fact,null);
        Assert.Equal(ManeuverKind.Straight,Assert.Single(result.Actions).Kind);Assert.Empty(result.Issues);
    }
    [Fact]
    public void InvalidSelectedOutcomePropagatesThroughTheComponentPlannerWithoutALocalCatch()
    {
        NativeLaneCurve Curve(int id,Vector3 start,Vector3 finish,int[] next,int[] previous)
            =>new(id,start,finish,finish-start,finish-start,next,previous);
        var geometry=new NativeLaneGeometry(42,[Curve(0,Vector3.Zero,new(90,0,0),[2],[]),
            Curve(1,new(0,0,30),new(90,0,0),[2],[]),Curve(2,new(90,0,0),new(100,0,0),[],[0,1])])
            {InputCurveIndices=[0,1],OutputCurveIndices=[2]};
        DirectedRoad Path(int first,ulong node)
        {
            Vector3[] points=[geometry.Curves[first].Start,new(90,0,0),new(100,0,0)];
            var lane=new PrefabLanePath(42,node,30,0,0,0,0,1,1,[first,2],points,new LanePolyline(points).Length)
                {NativeGeometry=geometry,CurveStartPointIndices=[0,1]};
            return new($"prefab:2A:{node}:30",node,30,points){LanePaths=[lane],JoinPriority=new(10,[],[],8,"main")};
        }
        var main=Path(0,10);var side=Path(1,20);var graph=new NativeLaneGraph([main,side]);
        var planner=new ComponentManeuverPlanner();
        var chosen=new LaneNavigationPlan([new(side.Id,side.LanePaths[0],0)],[],true,null);
        Assert.Equal(ManeuverKind.KeepRight,Assert.Single(planner.Build([side],graph.Edges,chosen)[0]).Kind);
        var invalid=chosen with{Sections=[new(side.Id,side.LanePaths[0] with{EntryOrder=9},0)]};
        var error=Assert.Throws<ArgumentException>(()=>planner.Build([side],graph.Edges,invalid));
        Assert.Contains("9->0",error.Message);
    }

}
