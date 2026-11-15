using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using BlinkerType=TruckLib.Models.Ppd.BlinkerType;

namespace LinkEts2Nav.UnitTests;

public sealed class InheritedForkTests
{
    [Theory]
    [InlineData(BlinkerType.RightBlinker)]
    [InlineData(BlinkerType.LeftBlinker)]
    public void ThreeLanesInheritedAsTwoPlusOneAreLaneSelectionsDespiteTheirLamp(BlinkerType lamp)
    {
        var curves=Enumerable.Range(0,6).Select(_=>new NavCurve()).ToList();
        curves[0].NextLines.Add(3);curves[1].NextLines.Add(4);curves[2].NextLines.Add(5);curves[5].Blinker=lamp;
        var descriptor=new PrefabDescriptor{NavCurves=curves,Nodes=[
            new(){InputLines=[0,1,2],OutputLines=[]},
            new(){Position=new(0,0,-100),InputLines=[],OutputLines=[3,4]},
            new(){Position=new(30,0,-100),InputLines=[],OutputLines=[5]}]};
        var prefab=new Prefab{Nodes=descriptor.Nodes.Select((n,i)=>(INode)new Node{Uid=(ulong)(10+i),Position=n.Position}).ToList()};
        Assert.True(PrefabPlacement.TryCreate(prefab,descriptor,out var placement));
        var info=PrefabForkTopology.Build(prefab,descriptor,placement,_=>new Dictionary<int,int>{{0,0},{1,1},{2,2}})[10];
        Assert.All(info.Branches,b=>Assert.True(b.HasInheritedLane));
        Assert.Equal(lamp==BlinkerType.RightBlinker?ForkSignal.Right:ForkSignal.Left,info.Branches.Single(b=>b.ExitNode==12).Signal);
        Assert.Equal("分岔口靠左，走左侧两车道",RoadForkRules.Decide(info,11).Text);
        Assert.Equal("分岔口靠右，走最右侧车道",RoadForkRules.Decide(info,12).Text);
    }

    [Fact]
    public void InheritedOutletDoesNotNeedConsistentLampCommandsToIdentifyItsLaneGroup()
    {
        var info=new RoadForkInfo(10,3,[new(20,null,[0,1],[0,1],[],true,"mixed lamps"),
            new(30,ForkSignal.Right,[2],[2],[],true)]);
        Assert.Equal("分岔口靠左，走左侧两车道",RoadForkRules.Decide(info,20).Text);
        Assert.Equal("mixed lamps",info.Branches[0].Failure);
    }

    [Fact]
    public void WhollyNewLitBranchRetainsTheTurnInstruction()
    {
        var info=new RoadForkInfo(10,2,[new(20,ForkSignal.None,[0,1],[0,1],[],true),
            new(30,ForkSignal.Right,[1],[1],[],false)]);
        Assert.True(RoadForkRules.Decide(info,20).Silent);
        Assert.Equal(ManeuverKind.KeepRight,RoadForkRules.Decide(info,30).Kind);
    }
}
