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
        var info=new RoadForkInfo(1,2,[Branch(2,ForkSignal.None,[0,1]),Branch(3,ForkSignal.Right,[1],false)]);
        Assert.True(RoadForkRules.Decide(info,2).Silent);
        Assert.Equal(ManeuverKind.KeepRight,RoadForkRules.Decide(info,3).Kind);
        Assert.Empty(RoadForkRules.Decide(info,3).DedicatedOrders);
        Assert.Equal(ManeuverKind.KeepLeft,RoadForkRules.Decide(info with{Branches=[Branch(3,ForkSignal.Left,[0],false)]},3).Kind);
    }
    [Fact]
    public void MultipleUnlitExitsUseReachableEntranceLaneGroups()
    {
        var info=new RoadForkInfo(1,3,[Branch(2,ForkSignal.None,[0,1]),Branch(3,ForkSignal.None,[2])]);
        Assert.Equal("分岔口靠左，走左侧两车道",RoadForkRules.Decide(info,2).Text);
        Assert.Equal("分岔口靠右，走最右侧车道",RoadForkRules.Decide(info,3).Text);
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
    public void BinaryForkOrdersNativeTangentsAndKeepsItsLaneInstruction()
    {
        var left=Branch(2,ForkSignal.None,[0,1]) with{ExitTurnRadians=0};
        var right=Branch(3,ForkSignal.None,[2,3]) with{ExitTurnRadians=Math.PI/12};
        var info=new RoadForkInfo(1,4,[right,left]);
        Assert.Equal("分岔口靠左，走左侧两车道",RoadForkRules.Decide(info,2).Text);
        Assert.Equal("分岔口靠右，走右侧两车道",RoadForkRules.Decide(info,3).Text);
        Assert.Equal(ManeuverKind.ChooseLanes,RoadForkRules.Decide(info,3).Kind);
        var instruction=RoadForkRules.Decide(info,3);
        Assert.Equal("两百米后，分岔口靠右，走右侧两车道。",NavigationPrompts.AtDistance(instruction.Kind,200,0,instruction.Text));
        Assert.Equal("分岔口靠右，走右侧两车道。",NavigationPrompts.Execute(instruction.Kind,instruction.Text));
    }

    [Fact]
    public void ThreeExitInterfacesUseMultipleExitWordingIncludingLitInterfaces()
    {
        var info=new RoadForkInfo(1,5,[Branch(2,ForkSignal.None,[0]),Branch(3,ForkSignal.None,[1,3]),Branch(4,ForkSignal.Right,[4],false)]);
        Assert.Equal("有多个相近出口，请走最左侧车道",RoadForkRules.Decide(info,2).Text);
        Assert.Equal("有多个相近出口，请走右侧第二、第四车道",RoadForkRules.Decide(info,3).Text);
        Assert.Equal(ManeuverKind.KeepRight,RoadForkRules.Decide(info,4).Kind);
        Assert.Null(RoadForkRules.Decide(info,4).Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(210)]
    public void NativeOutletOrderIsRelativeToEntranceNotWorldAxes(float rotationDegrees)
    {
        var heading=Quaternion.CreateFromAxisAngle(Vector3.UnitY,rotationDegrees*MathF.PI/180);
        var curves=Enumerable.Range(0,4).Select(_=>new NavCurve{StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity}).ToList();
        curves[0].NextLines.Add(2);curves[1].NextLines.Add(3);
        curves[3].EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/12);
        var descriptor=new PrefabDescriptor{Nodes=[new(){InputLines=[0,1],OutputLines=[]},
            new(){Position=new(0,0,-100),InputLines=[],OutputLines=[2]},
            new(){Position=new(30,0,-100),InputLines=[],OutputLines=[3]}],NavCurves=curves};
        var prefab=new Prefab{Nodes=descriptor.Nodes.Select((n,i)=>(INode)new Node{Uid=(ulong)(10+i),
            Position=Vector3.Transform(n.Position,heading),Rotation=heading}).ToList()};
        Assert.True(PrefabPlacement.TryCreate(prefab,descriptor,out var placement));
        var info=PrefabForkTopology.Build(prefab,descriptor,placement,_=>new Dictionary<int,int>{{0,0},{1,1}})[10];
        Assert.Equal(0,info.Branches.Single(b=>b.ExitNode==11).ExitTurnRadians!.Value,5);
        Assert.Equal(Math.PI/12,info.Branches.Single(b=>b.ExitNode==12).ExitTurnRadians!.Value,5);
        Assert.Equal("分岔口靠右，走右侧车道",RoadForkRules.Decide(info,12).Text);
    }
    [Fact]
    public void IncompleteOrIndistinguishableBranchesHaveDiagnosticsAndNeverGuessAngles()
    {
        var info=new RoadForkInfo(1,1,[Branch(2,ForkSignal.None,[0]),Branch(3,ForkSignal.None,[0])]);
        Assert.NotNull(RoadForkRules.Decide(info,2).Failure);
        info=info with{EntryLaneCount=2,Branches=[Branch(2,ForkSignal.None,[0]),Branch(3,null,[1],false)]};
        Assert.NotNull(RoadForkRules.Decide(info,2).Failure);
        Assert.NotNull(RoadForkRules.Decide(info,3).Failure);
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
    [InlineData(0,ManeuverKind.ChooseLanes,"分岔口靠右，走最右侧车道")]
    [InlineData(1,ManeuverKind.ChooseLanes,"分岔口靠右，走最右侧车道")]
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
        if(fixture is 0 or 1)Assert.Equal("分岔口靠左，走左侧两车道",retained.Text);else Assert.True(retained.Silent);
    }

}
