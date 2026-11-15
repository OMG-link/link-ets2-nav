using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class ParallelLaneIndexTests
{
    [Theory][InlineData(499,true)][InlineData(500,true)][InlineData(600,false)]
    public void CompleteParallelFactsDoNotChangeTheShortAuxiliaryPolicy(int length,bool shortAuxiliary)
    {
        PrefabLanePath Path(ulong prefab,ulong input,ulong output,int from,int to,int inputs,int outputs)=>
            new(prefab,input,output,from,to,from,to,inputs,outputs,[from*10+to],
                [new(0,0,0),new(10,0,0)],10){CurveStartPointIndices=[0],CurveBlinkers=[LaneBlinker.NoBlinker]};
        var main=new DirectedRoad("prefab:1:0:1",10,20,[new(0,0,0),new(10,0,0)])
            {LanePaths=[Path(1,10,20,0,0,1,2)],LaneConnections=[new(10,0,20,0),new(11,0,20,1)],JoinPriority=new(10,[],[],20,"fixture")};
        var side=new DirectedRoad("prefab:1:2:1",11,20,main.Points){LanePaths=[Path(1,11,20,0,1,1,2)]};
        var road=new DirectedRoad("parallel",20,30,[new(10,0,0),new(10+length,0,0)]);
        var kept=new DirectedRoad("prefab:2:0:1",30,40,[new(10+length,0,0),new(20+length,0,0)])
            {LanePaths=[Path(2,30,40,0,0,2,1)]};
        var exit=new DirectedRoad("prefab:2:0:2",30,41,kept.Points){LanePaths=[Path(2,30,41,1,0,2,1)]};
        var index=AuxiliaryLaneIndex.Build(new NativeLaneGraph([main,side,road,kept,exit]),TestContext.Current.CancellationToken);
        Assert.Equal(length,Assert.Single(index.ParallelCorridors).LengthMeters);
        Assert.Equal(shortAuxiliary,index.Corridors.Count==1);
        Assert.Equal(shortAuxiliary,index.ByRoad.ContainsKey(NativeRoadKey.Of(road)));
        Assert.Empty(index.Diagnostics);
    }
}
