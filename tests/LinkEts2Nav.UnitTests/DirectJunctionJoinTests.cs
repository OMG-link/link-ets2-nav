using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class DirectJunctionJoinTests
{
    private static PrefabLanePath Lane(ulong entry,int input,int output,int[] curves,LaneBlinker[] lamps)
        =>new(10,entry,3,input,output,input,output,entry==2?1:2,2,curves,
            [new(0,0,0),new(0,0,10)],10){CurveBlinkers=lamps};
    private static (DirectedRoad Side,DirectedRoad Main,DirectedRoad Other) Scene()
    {
        var side=new DirectedRoad("prefab:X:2:1",2,3,[new(0,0,-10),new(0,0,0),new(10,0,0)])
        {Junction=new(2,2,0){InterfaceCount=3},NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitX,
            LanePaths=[Lane(2,0,1,[2,12,1],[LaneBlinker.NoBlinker,LaneBlinker.Right,LaneBlinker.NoBlinker])]};
        var main=side with{Id="prefab:X:0:1",StartNodeUid=1,NativeEntryTangent=Vector3.UnitX,
            LanePaths=[Lane(1,1,1,[5,13,1],[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker])]};
        var other=main with{Id="prefab:X:0:2",EndNodeUid=4,LanePaths=[]};
        return(side,main,other);
    }

    [Fact]
    public void SharedNativeTailRecognizesDirectJoinEvenWhenMainHasAnotherOutlet()
    {
        var(s,m,o)=Scene();
        var decision=DirectJunctionJoin.Find(s,[s,m,o]);
        Assert.NotNull(decision);Assert.Equal(NavigationSide.Right,decision.TrafficSide);
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([s],0,[s,m,o]));
        Assert.Equal(ManeuverKind.TurnLeft,action.Kind);
        Assert.Equal("左转，注意右侧来车",action.ExecutionInstruction);
        Assert.Null(action.Instruction);Assert.Null(action.NearInstruction);
        Assert.True(action.EndDistanceMeters>0);
    }

    [Fact]
    public void SharedOutputWithoutSharedNativeCurveIsNotDirectJoin()
    {
        var(s,m,o)=Scene();m=m with{LanePaths=[m.LanePaths[0] with{CurveIndices=[5,13,7]}]};
        Assert.Null(DirectJunctionJoin.Find(s,[m,o]));
    }

    [Theory]
    [InlineData(30,ManeuverKind.KeepRight)]
    [InlineData(90,ManeuverKind.TurnRight)]
    [InlineData(-30,ManeuverKind.KeepLeft)]
    public void DirectionUsesNativeInterfaceTangents(double angle,ManeuverKind expected)
    {
        var(s,m,o)=Scene();
        double radians=angle*Math.PI/180;
        s=s with{NativeEntryTangent=Vector3.UnitX,
            NativeExitTangent=new((float)Math.Cos(radians),0,(float)Math.Sin(radians))};
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([s],0,[s,m,o]));
        Assert.Equal(expected,action.Kind);
        Assert.Null(action.ExecutionInstruction); // Parallel approach does not identify a side of oncoming traffic.
    }

    [Theory]
    [InlineData(15,ManeuverKind.KeepRight,"向右前方行驶，注意左侧来车")]
    [InlineData(90,ManeuverKind.TurnRight,"右转，注意左侧来车")]
    [InlineData(-15,ManeuverKind.KeepLeft,"向左前方行驶，注意右侧来车")]
    [InlineData(-90,ManeuverKind.TurnLeft,"左转，注意右侧来车")]
    [InlineData(1,ManeuverKind.KeepRight,null)]
    public void SideTurnNamingAndMainTrafficWarningUseIndependentNativeDirections(double angle,ManeuverKind expected,string? warning)
    {
        var(s,m,o)=Scene();double radians=angle*Math.PI/180;
        s=s with{NativeEntryTangent=new((float)Math.Cos(radians),0,(float)-Math.Sin(radians)),NativeExitTangent=Vector3.UnitX,
            LanePaths=[s.LanePaths[0] with{CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.Left,LaneBlinker.NoBlinker]}]};
        var action=Assert.Single(new NavigationManeuverDetector().FindSequence([s],0,[s,m,o]));
        Assert.Equal(expected,action.Kind);Assert.Equal(warning,action.ExecutionInstruction);
        Assert.Null(action.Instruction);Assert.Null(action.NearInstruction);
        Assert.DoesNotContain(new NavigationManeuverDetector().FindSequence([m],0,[s,m,o]),a=>a.Id.StartsWith("direct-join:"));
    }

    [Fact]
    public void AmbiguousPriorityAndSameEntranceLaneReductionAreNotDirectJoin()
    {
        var(s,m,o)=Scene();
        var unknown=s with{LanePaths=[s.LanePaths[0] with{CurveBlinkers=[LaneBlinker.NoBlinker,LaneBlinker.NoBlinker,LaneBlinker.NoBlinker]}]};
        Assert.Null(DirectJunctionJoin.Find(unknown,[m,o]));
        Assert.Null(DirectJunctionJoin.Find(s,[m with{StartNodeUid=s.StartNodeUid}]));
        Assert.Null(DirectJunctionJoin.Find(s,[m,m with{Id="prefab:X:3:1",StartNodeUid=8}]));
    }
}
