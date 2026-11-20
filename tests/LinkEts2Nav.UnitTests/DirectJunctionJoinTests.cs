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
        // A legacy shared-tail/blinker summary is not sufficient production native geometry.
        var detector=new NavigationManeuverDetector();
        Assert.Empty(detector.FindSequence([s],0,[s,m,o]));
        Assert.Contains(detector.Components.Issues,i=>i is LinkEts2Nav.Map.MapQueryFailure);

    }

    [Fact]
    public void SharedOutputWithoutSharedNativeCurveIsNotDirectJoin()
    {
        var(s,m,o)=Scene();m=m with{LanePaths=[m.LanePaths[0] with{CurveIndices=[5,13,7]}]};
        Assert.Null(DirectJunctionJoin.Find(s,[m,o]));
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
