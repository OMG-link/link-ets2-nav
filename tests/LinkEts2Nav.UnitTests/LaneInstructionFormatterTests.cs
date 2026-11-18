using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class LaneInstructionFormatterTests
{
    [Theory]
    [InlineData(2,new[]{0},"走左侧车道")]
    [InlineData(2,new[]{1},"走右侧车道")]
    [InlineData(4,new[]{0},"走最左侧车道")]
    [InlineData(4,new[]{3},"走最右侧车道")]
    [InlineData(4,new[]{0,1},"走左侧两车道")]
    [InlineData(4,new[]{2,3},"走右侧两车道")]
    [InlineData(5,new[]{0,1,2},"走左侧三车道")]
    [InlineData(5,new[]{1,3},"走右侧第二、第四车道")]
    public void StructuredSelectionsHaveOneSharedGrammar(int total,int[] selected,string expected)
    {
        Assert.Equal(expected,LaneInstructionFormatter.Selection(total,selected));
        Assert.Equal(expected,RoadForkRules.LaneSelectionText(total,selected));
    }
    [Theory]
    [InlineData(2,NavigationSide.Left,1,"走左侧车道")]
    [InlineData(3,NavigationSide.Left,1,"走最左侧车道")]
    [InlineData(3,NavigationSide.Right,2,"走右侧两车道")]
    [InlineData(4,NavigationSide.Left,3,"走左侧三车道")]
    [InlineData(null,NavigationSide.Right,1,"走右侧车道")]
    public void OuterSelectionsUseTheSameGrammar(int? total,NavigationSide side,int count,string expected)
        =>Assert.Equal(expected,LaneInstructionFormatter.EdgeSelection(total,side,count));
    [Fact]
    public void AllLanesOrInvalidSelectionIsNotAnInstruction()
    {
        Assert.Null(LaneInstructionFormatter.Selection(2,[0,1]));
        Assert.Null(LaneInstructionFormatter.Selection(2,[2]));
        Assert.Null(LaneInstructionFormatter.EdgeSelection(2,NavigationSide.Right,2));
        Assert.Null(LaneInstructionFormatter.EdgeSelection(3,NavigationSide.None,1));
    }
    [Fact]
    public void NativeThreeToTwoLaneReductionUsesSharedRightSideWordingAtBothPreviewStages()
    {
        var main=new List<Vector3>();var ending=new List<Vector3>();var other=new List<Vector3>();
        for(int d=45;d>=-10;d--)
        {
            double t=Math.Clamp(d/30d,0,1);float offset=(float)(-4.5*(3*t*t-2*t*t*t));
            main.Add(new(0,0,d));ending.Add(new(offset,0,d));other.Add(new(4.5f,0,d));
        }
        PrefabLanePath Lane(int lane,int exit,int curve,List<Vector3> points)=>new(42,10,20,lane,exit,lane,exit,3,2,[curve,exit==0?9:10],points,55)
            {CurveBlinkers=[lane==0?LaneBlinker.Right:LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]};
        var left=Lane(0,0,0,ending);var center=Lane(1,0,1,main);var right=Lane(2,1,2,other);
        var edge=new DirectedRoad("prefab:000000000000002A:0:1",10,20,main){LanePaths=[left,center,right]};
        var road=new DirectedRoad("road",5,10,Enumerable.Range(0,651).Select(i=>new Vector3(0,0,695-i)).ToArray());
        DirectedRoad[] route=[road,edge];var lanes=new LaneNavigationPlan([new(edge.Id,left,1)],[],true,null);
        var paths=new RoadPathSource(new TruckLib.ScsMap.Map(),[edge],native:new NativeLaneGraph(route));
        var map=new NearbyMapSnapshot(new TruckLib.ScsMap.Map(),[],[],[]){PrefabPaths=[edge]};
        var result=new NavigationStructurePlanner().Build(route,new([],"fixture",RouteAvailability.None,DateTimeOffset.UtcNow),map,paths,lanes);
        var merge=Assert.Single(result.Merges);Assert.Equal(3,merge.ApproachLaneCount);Assert.Equal(2,merge.RecommendedLaneCount);
        var action=Assert.Single(result.Actions);
        Assert.Equal("车道合并，建议提前走右侧两车道",action.Instruction);
        foreach(int distance in new[]{500,200})
            Assert.Equal((distance==500?"五百":"两百")+"米后，车道合并，建议提前走右侧两车道。",
                NavigationPrompts.AtDistance(action.Kind,distance,0,action.Instruction));
    }
}
