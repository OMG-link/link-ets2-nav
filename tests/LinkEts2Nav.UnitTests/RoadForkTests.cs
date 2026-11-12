using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using Blinker = TruckLib.Models.Ppd.BlinkerType;

namespace LinkEts2Nav.UnitTests;

public sealed class RoadForkTests
{
    private static RoadForkBranch Branch(ulong exit,ForkSignal? signal,int[] lanes,bool inherited=true)
        =>new(exit,signal,lanes,lanes,[],inherited);
    [Fact]
    public void OnlyUnlitExitIsSilentButLitExitUsesItsOwnLamp()
    {
        var info=new RoadForkInfo(1,2,[Branch(2,ForkSignal.None,[0,1]),Branch(3,ForkSignal.Right,[1])]);
        Assert.True(RoadForkRules.Decide(info,2).Silent);
        Assert.Equal(ManeuverKind.KeepRight,RoadForkRules.Decide(info,3).Kind);
        Assert.Empty(RoadForkRules.Decide(info,3).DedicatedOrders);
        Assert.Equal(ManeuverKind.KeepLeft,RoadForkRules.Decide(info with{Branches=[Branch(3,ForkSignal.Left,[0])]},3).Kind);
    }
    [Fact]
    public void MultipleUnlitExitsUseReachableEntranceLaneGroups()
    {
        var info=new RoadForkInfo(1,3,[Branch(2,ForkSignal.None,[0,1]),Branch(3,ForkSignal.None,[2])]);
        Assert.Equal("走左侧两车道",RoadForkRules.Decide(info,2).Text);
        Assert.Equal("走最右侧车道",RoadForkRules.Decide(info,3).Text);
        Assert.Equal(new[]{2},RoadForkRules.Decide(info,3).DedicatedOrders);
    }
    [Theory]
    [InlineData(2,new[]{0},"走左侧车道")]
    [InlineData(2,new[]{1},"走右侧车道")]
    [InlineData(4,new[]{0},"走最左侧车道")]
    [InlineData(4,new[]{3},"走最右侧车道")]
    [InlineData(4,new[]{2,3},"走右侧两车道")]
    [InlineData(5,new[]{1,3},"走右侧第二、第四车道")]
    [InlineData(4,new[]{1,2},"走右侧第二、第三车道")]
    public void LaneWordingUsesEntranceOrder(int count,int[] lanes,string text)
        =>Assert.Equal(text,RoadForkRules.LaneSelectionText(count,lanes));
    [Fact]
    public void IncompleteOrIndistinguishableBranchesHaveDiagnosticsAndNeverGuessAngles()
    {
        var info=new RoadForkInfo(1,1,[Branch(2,ForkSignal.None,[0]),Branch(3,ForkSignal.None,[0])]);
        Assert.NotNull(RoadForkRules.Decide(info,2).Failure);
        info=info with{EntryLaneCount=2,Branches=[Branch(2,ForkSignal.None,[0]),Branch(3,null,[1])]};
        Assert.NotNull(RoadForkRules.Decide(info,2).Failure);
        Assert.NotNull(RoadForkRules.Decide(info,3).Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void IdenticalUnlitLaneSetsRequestMoreSceneInformationBeforeInheritanceChecks(int laneCount)
    {
        // Synthetic boundary case, not a claim that this exists in the game map.
        var info=new RoadForkInfo(10,laneCount,
            [Branch(20,ForkSignal.None,[0],false),Branch(30,ForkSignal.None,[0],false)]);
        var instruction=RoadForkRules.Decide(info,20);
        Assert.True(instruction.Silent);
        Assert.Null(instruction.Text);
        Assert.Contains("需要该场景的更多信息才能决定如何处理",instruction.Failure);
        Assert.Contains("入口总车道数="+laneCount,instruction.Failure);
        Assert.Contains("出口=0000000000000014",instruction.Failure);
        Assert.Contains("出口=000000000000001E",instruction.Failure);
        Assert.Contains("可用入口车道（左起1）=[1]",instruction.Failure);
        var approach=new DirectedRoad("road",1,10,[Vector3.Zero,new(0,0,100)]);
        var selected=new DirectedRoad("prefab:1:0:1",10,20,[new(0,0,100),new(0,0,200)]){ForkInfo=info};
        var detector=new NavigationManeuverDetector();
        Assert.Empty(detector.FindSequence([approach,selected],0,[approach,selected]));
        var diagnostic=Assert.Single(detector.ForkDiagnostics);
        Assert.Equal("1",diagnostic.Prefab);
        Assert.Equal(10UL,diagnostic.EntryNode);
        Assert.Equal(20UL,diagnostic.ExitNode);
        Assert.Equal(instruction.Failure,diagnostic.Reason);
    }
    private static RoadForkInfo Topology(Blinker delayed,bool extraConflictingPath=false)
    {
        // 0 is a shared approach. Both immediate outgoing curves are unlit.
        // The selected branch's command arrives later, followed by a clearing curve.
        var curves=new List<NavCurve>{new(){NextLines={1,2}},new(){NextLines={3}},new(){NextLines={4}},
            new(){NextLines={5},Blinker=delayed},new(),new()};
        if(extraConflictingPath){curves[2].NextLines.Add(6);curves.Add(new(){NextLines={5},Blinker=Blinker.LeftBlinker});}
        var d=new PrefabDescriptor{Nodes=[new(){Position=Vector3.Zero,InputLines=[0],OutputLines=[]},
            new(){Position=new(0,0,100),InputLines=[],OutputLines=[4]},
            new(){Position=new(100,0,100),InputLines=[],OutputLines=[5]}],NavCurves=curves};
        var p=new Prefab{Nodes=[new Node{Uid=10,Position=Vector3.Zero},new Node{Uid=20,Position=new(0,0,100)},new Node{Uid=30,Position=new(100,0,100)}]};
        Assert.True(PrefabPlacement.TryCreate(p,d,out var placement));
        return PrefabForkTopology.Build(p,d,placement,_=>new Dictionary<int,int>{{0,0}})[10];
    }
    [Fact]
    public void ReadsLaterBranchLampAndIgnoresTerminalClearing()
    {
        var info=Topology(Blinker.RightBlinker);
        Assert.Equal(ForkSignal.None,info.Branches.Single(b=>b.ExitNode==20).Signal);
        Assert.Equal(ForkSignal.Right,info.Branches.Single(b=>b.ExitNode==30).Signal);
        Assert.Contains(info.Branches.Single(b=>b.ExitNode==30).Decisions,p=>p.FromCurve==0&&p.ToCurve==1);
    }
    [Fact]
    public void FullNativeAlternativesExposeConflictMissingFromShortestRepresentative()
        =>Assert.Null(Topology(Blinker.RightBlinker,true).Branches.Single(b=>b.ExitNode==30).Signal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleExitLaneBirthReadsWholeNativeDivergenceAndExcludesEarlierLamp(bool conflicting)
    {
        var curves=new List<NavCurve>{new(),new(){Blinker=Blinker.LeftBlinker,NextLines={2}},new(){NextLines={3,4}},
            new(),new(){Blinker=Blinker.RightBlinker,NextLines={5}},new()};
        if(conflicting){curves[2].NextLines.Add(6);curves.Add(new(){Blinker=Blinker.LeftBlinker,NextLines={5}});}
        var d=new PrefabDescriptor{Nodes=[new(){Position=Vector3.Zero,InputLines=[0,1],OutputLines=[]},
            new(){Position=new(0,0,100),InputLines=[],OutputLines=[0,3,5]}],NavCurves=curves};
        var p=new Prefab{Nodes=[new Node{Uid=10,Position=Vector3.Zero},new Node{Uid=20,Position=new(0,0,100)}]};
        Assert.True(PrefabPlacement.TryCreate(p,d,out var placement));
        var signals=PrefabForkTopology.LaneSignals(p,d,placement,_=>new Dictionary<int,int>{{0,0},{1,1}});
        Assert.Equal(ForkSignal.None,signals[(10,1,20,1)]);
        if(conflicting)Assert.Null(signals[(10,1,20,2)]);else Assert.Equal(ForkSignal.Right,signals[(10,1,20,2)]);
    }

    // Minimal real PPD topology fixtures: indices, connectivity and lamp commands
    // only. No model meshes, textures or full game assets are embedded.
    [Theory]
    [InlineData(0,ManeuverKind.ChooseLanes,"走最右侧车道")]
    [InlineData(1,ManeuverKind.KeepRight,null)]
    [InlineData(2,ManeuverKind.KeepRight,null)]
    public void RetainedRealNativeForksFollowSignalAndLaneRules(int fixture,ManeuverKind expected,string? text)
    {
        int[][] next;int[] inputs,main,exit;int lamp;
        switch(fixture)
        {
            // balt_hw2_exit_joint_mirrored_tmpl.ppd
            case 0:next=[[2],[],[],[]];inputs=[3,1,0];main=[3,1];exit=[2];lamp=-1;break;
            // un_fork_1m-4m_0-3_0-2_split_0-1_40m.ppd
            case 1:next=[[1],[6],[3],[7],[5],[9],[],[],[],[8]];inputs=[0,2,4];main=[6,7];exit=[8];lamp=9;break;
            // un_fork_1m-4m_0-2_divide_0-1_60m.ppd
            default:next=[[],[2],[],[4],[5],[],[7,8],[1],[9],[0]];inputs=[3,6];main=[5,0];exit=[2];lamp=1;break;
        }
        var curves=next.Select((n,i)=>{var curve=new NavCurve{Blinker=i==lamp?Blinker.RightBlinker:Blinker.NoBlinker};
            foreach(int child in n)curve.NextLines.Add(child);return curve;}).ToList();
        var d=new PrefabDescriptor{Nodes=[new(){Position=Vector3.Zero,InputLines=inputs,OutputLines=[]},
            new(){Position=new(0,0,100),InputLines=[],OutputLines=main},new(){Position=new(100,0,100),InputLines=[],OutputLines=exit}],NavCurves=curves};
        var p=new Prefab{Nodes=[new Node{Uid=10,Position=Vector3.Zero},new Node{Uid=20,Position=new(0,0,100)},new Node{Uid=30,Position=new(100,0,100)}]};
        Assert.True(PrefabPlacement.TryCreate(p,d,out var placement));
        var info=PrefabForkTopology.Build(p,d,placement,_=>Enumerable.Range(0,inputs.Length).ToDictionary(i=>i,i=>i))[10];
        var selected=RoadForkRules.Decide(info,30);
        Assert.Equal(expected,selected.Kind);Assert.Equal(text,selected.Text);Assert.Null(selected.Failure);
        var retained=RoadForkRules.Decide(info,20);
        if(fixture==0)Assert.Equal("走左侧两车道",retained.Text);else Assert.True(retained.Silent);
    }

    [Fact]
    public void OrdinaryForkNeedsNoCrossingJunctionAndDoesNotUseExitAngle()
    {
        var info=new RoadForkInfo(10,3,[Branch(20,ForkSignal.None,[0,1]),Branch(30,ForkSignal.Right,[2])]);
        DirectedRoad approach=new("road",1,10,[new(0,0,0),new(0,0,100)]);
        DirectedRoad main=new("prefab:1:0:1",10,20,[new(0,0,100),new(-100,0,200)]){ForkInfo=info};
        DirectedRoad side=new("prefab:1:0:2",10,30,[new(0,0,100),new(-100,0,150)]){ForkInfo=info};
        var detector=new NavigationManeuverDetector();
        Assert.Empty(detector.FindSequence([approach,main],0,[approach,main,side]));
        Assert.Equal(ManeuverKind.KeepRight,Assert.Single(detector.FindSequence([approach,side],0,[approach,main,side])).Kind);
        Assert.Empty(detector.ForkDiagnostics);
    }
}
