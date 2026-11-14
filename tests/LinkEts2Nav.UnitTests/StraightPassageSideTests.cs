using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;

namespace LinkEts2Nav.UnitTests;

public sealed class StraightPassageSideTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-14T00:00:00Z");
    private static NavigationManeuver Straight(string id,double distance,NavigationSide exits)=>new(id,ManeuverKind.Straight,distance,"in","out")
        {JunctionId=id,EndDistanceMeters=distance+30,StraightExitSides=exits};
    private static NavigationManeuver Target(ManeuverKind kind)=>new("target",kind,1000,"in","out")
        {JunctionId="target",EndDistanceMeters=1030};
    private static GuidanceSourcePlan Plan(params NavigationManeuver[] maneuvers)
        =>new NavigationEventPlanner().Build("s","r",Now,0,maneuvers,true,false,false,new());
    private static SpeechTrigger[] Passages(GuidanceSourcePlan plan)=>plan.Triggers.Where(t=>t.Id.Contains(":through:")).ToArray();

    [Theory]
    [InlineData(ManeuverKind.TurnLeft,NavigationSide.Left)]
    [InlineData(ManeuverKind.KeepLeft,NavigationSide.Left)]
    [InlineData(ManeuverKind.MergeLeft,NavigationSide.Left)]
    [InlineData(ManeuverKind.TurnRight,NavigationSide.Right)]
    [InlineData(ManeuverKind.KeepRight,NavigationSide.Right)]
    [InlineData(ManeuverKind.MergeRight,NavigationSide.Right)]
    public void DirectionalActionsOnlyRemindAtTheSameSideExit(ManeuverKind kind,NavigationSide side)
    {
        var other=side==NavigationSide.Left?NavigationSide.Right:NavigationSide.Left;
        var plan=Plan(Straight("opposite",600,other),Straight("same",700,side),Target(kind));
        var helper=Assert.Single(Passages(plan));
        Assert.Contains("same@",helper.Id);
        Assert.Equal("直行通过当前路口。",helper.Text);
    }

    [Theory]
    [InlineData(ManeuverKind.EnterRoundabout)]
    [InlineData(ManeuverKind.Destination)]
    [InlineData(ManeuverKind.BoardFerry)]
    [InlineData(ManeuverKind.UTurn)]
    [InlineData(ManeuverKind.ChooseLanes)]
    public void TargetsWithoutADirectionHaveNoIndividualPassageReminders(ManeuverKind kind)
        =>Assert.Empty(Passages(Plan(Straight("both",850,NavigationSide.Left|NavigationSide.Right),Target(kind))));

    [Theory]
    [InlineData(2,NavigationSide.Left)]
    [InlineData(3,NavigationSide.Right)]
    public void LaneSelectionUsesTheStructuredSideAlreadyChosenByForkWording(ulong exit,NavigationSide side)
    {
        var fork=new RoadForkInfo(1,4,[new(2,ForkSignal.None,[0,1],[0,1],[],true),new(3,ForkSignal.None,[2,3],[2,3],[],true)]);
        var choice=RoadForkRules.Decide(fork,exit);
        Assert.Equal(side,choice.Side);
        Assert.StartsWith(side==NavigationSide.Left?"分岔口靠左":"分岔口靠右",choice.Text);
        var other=side==NavigationSide.Left?NavigationSide.Right:NavigationSide.Left;
        var plan=Plan(Straight("opposite",600,other),Straight("same",700,side),Target(ManeuverKind.ChooseLanes) with{ForkInstruction=choice});
        Assert.Contains("same@",Assert.Single(Passages(plan)).Id);
    }

    [Fact]
    public void MultipleNearbyOutletSelectionHasNoDirectionalPassageReminders()
    {
        var fork=new RoadForkInfo(1,4,[new(2,ForkSignal.None,[0],[0],[],true),new(3,ForkSignal.None,[1],[1],[],true),new(4,ForkSignal.None,[2],[2],[],true)]);
        var choice=RoadForkRules.Decide(fork,2);
        Assert.Equal(NavigationSide.None,choice.Side);
        Assert.StartsWith("有多个相近出口",choice.Text);
        Assert.Empty(Passages(Plan(Straight("both",850,NavigationSide.Left|NavigationSide.Right),Target(ManeuverKind.ChooseLanes) with{ForkInstruction=choice})));
    }

    [Fact]
    public void FilteringRemindersDoesNotRenumberTheJunctions()
    {
        var plan=Plan(Straight("left",850,NavigationSide.Left),Straight("right",900,NavigationSide.Right),
            Straight("both",950,NavigationSide.Left|NavigationSide.Right),Target(ManeuverKind.TurnRight));
        Assert.Equal("两百米后，在第四个路口右转。",Assert.Single(plan.Triggers,t=>t.Id.EndsWith(":200")).Text);
        Assert.Equal(new[]{"直行通过第二个路口。","直行通过第三个路口。"},Passages(plan).Select(t=>t.Text));
    }

    [Fact]
    public void MissingExitEvidenceDoesNotInventAPassageReminder()
        =>Assert.Empty(Passages(Plan(Straight("unknown",850,NavigationSide.None),Target(ManeuverKind.TurnLeft))));

    [Fact]
    public void DenseJunctionSummaryIsIndependentOfDirectionalPassageReminders()
    {
        var plan=Plan(Straight("a",300,NavigationSide.None),Straight("b",400,NavigationSide.None),
            Straight("c",500,NavigationSide.None),Target(ManeuverKind.EnterRoundabout) with{DistanceMeters=2000,EndDistanceMeters=2030});
        Assert.Empty(Passages(plan));
        Assert.Contains(plan.Triggers,t=>t.Text=="注意在前方多个路口保持直行。");
    }

    private static Vector3 P(float x,float z)=>new(x,0,z);
    private static DirectedRoad Road(string id,ulong start,ulong end,Vector3 a,Vector3 b)=>new(id,start,end,[a,b]);
    private static DirectedRoad Connection(string id,ulong start,ulong end,Vector3 outlet)=>
        Road("prefab:SIDE:"+id,start,end,P(0,0),outlet) with
        {Junction=new(2,2,1),NativeEntryTangent=-Vector3.UnitZ,NativeExitTangent=outlet};

    [Theory]
    [InlineData(-1,NavigationSide.Left)]
    [InlineData(1,NavigationSide.Right)]
    public void DetectorUsesOnlyLegalConnectionsFromTheSelectedEntrance(int side,NavigationSide expected)
    {
        var before=Road("before",1,2,P(0,100),P(0,0));
        var straight=Connection("0:1",2,3,P(0,-50));
        var legal=Connection("0:2",2,4,P(side*50,0));
        var opposing=Connection("2:3",9,8,P(-side*50,0));
        var after=Road("after",3,5,P(0,-50),P(0,-100));
        var maneuver=Assert.Single(new NavigationManeuverDetector().FindSequence([before,straight,after],0,[before,straight,legal,opposing,after]));
        Assert.Equal(ManeuverKind.Straight,maneuver.Kind);
        Assert.Equal(expected,maneuver.StraightExitSides);
    }

    [Fact]
    public void OpposingOnlyPortIsNotALegalExitForThisPassage()
    {
        var before=Road("before",1,2,P(0,100),P(0,0));var straight=Connection("0:1",2,3,P(0,-50));
        var opposing=Connection("2:3",9,8,P(50,0));var after=Road("after",3,5,P(0,-50),P(0,-100));
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([before,straight,after],0,[before,straight,opposing,after]));
        Assert.Equal(NavigationSide.None,action.StraightExitSides);
    }

    [Fact]
    public void RampPassageUsesTheNativeSignalRatherThanAnOppositeGeometricBend()
    {
        var before=Road("before",1,2,P(0,100),P(0,0));
        var info=new RoadForkInfo(2,2,[new(3,ForkSignal.None,[0,1],[0,1],[],true),new(4,ForkSignal.Right,[1],[1],[],false)]);
        var straight=Connection("0:1",2,3,P(0,-50)) with{Junction=new(2,2,0){InterfaceCount=3},ForkInfo=info};
        var branch=Connection("0:2",2,4,P(-50,0)) with{Junction=straight.Junction,ForkInfo=info};
        var after=Road("after",3,5,P(0,-50),P(0,-100));
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([before,straight,after],0,[before,straight,branch,after]));
        Assert.Equal(ManeuverKind.Straight,action.Kind);
        Assert.Equal(NavigationSide.Right,action.StraightExitSides);
    }
}
