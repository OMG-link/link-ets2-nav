using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneSourceIdentityTests
{
    private static (NativeLaneGraph Graph,PrefabLanePath Ending,PrefabLanePath Main) Scene(bool allDead=false,bool missing=false,bool unchanged=false,double roadLength=100)
    {
        NativeLaneCurve Curve(int id,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(id,a,b,b-a,b-a,next,previous);
        Vector3 common=new(40,0,-4);
        var curves=unchanged?new[]{Curve(0,new(0,0,-4),new(50,0,-4),[],[]),Curve(4,Vector3.Zero,new(50,0,0),[],[])}:
            new[]{Curve(0,new(0,0,allDead?-24:-4),common,[3],[]),Curve(1,new(0,0,16),common,[3],[]),
                Curve(3,common,new(50,0,-4),[],[0,1]),Curve(4,Vector3.Zero,new(50,0,0),[],[])};
        var geometry=new NativeLaneGeometry(42,curves);
        PrefabLanePath Path(ulong prefab,ulong input,ulong output,int entry,int exit,int count,int[] chain,Vector3[] points,NativeLaneGeometry? native)
            =>new(prefab,input,output,entry,exit,entry,exit,count,2,chain,points,
                points.Zip(points.Skip(1)).Sum(p=>(double)Vector3.Distance(p.First,p.Second))){NativeGeometry=native,CurveStartPointIndices=Enumerable.Range(0,chain.Length).ToArray()};
        var left=Path(42,1,100,0,0,2,unchanged?[0]:[0,3],unchanged?[new(0,0,-4),new(50,0,-4)]:[curves[0].Start,common,new(50,0,-4)],missing?null:geometry);
        var right=Path(42,1,100,1,1,2,[4],[Vector3.Zero,new(50,0,0)],missing?null:geometry);
        var source=new List<DirectedRoad>{new("prefab:2A:1:100",1,100,left.Points){LanePaths=[left,right]}};
        if(!unchanged)
        {
            var turn=Path(42,2,100,0,0,1,[1,3],[curves[1].Start,common,new(50,0,-4)],missing?null:geometry);
            source.Add(new("prefab:2A:2:100",2,100,turn.Points){LanePaths=[turn]});
        }
        var main=Path(99,900,901,0,0,2,[0],[new(150,0,-4),new(160,0,-4)],null);
        var ending=Path(99,900,901,1,0,2,[1],[new(150,0,0),new(160,0,-4)],null);
        source.Add(new("prefab:63:900:901",900,901,main.Points){LanePaths=[main,ending]});
        source.Add(new("road",100,900,[new(50,0,-2),new((float)(50+roadLength),0,-2)]));
        source.Add(new("old-road",11,1,[new(-600,0,-2),new(0,0,-2)]));
        return(new(source),ending,main);
    }
    [Fact]
    public void DeadTurningPredecessorIsNotASecondIdentitySource()
    {
        var s=Scene();var result=new LaneMergeOriginClassifier(s.Graph).Classify(s.Ending,10,s.Main,10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);Assert.Null(result.MapIssue);
        Assert.Contains(result.Steps,p=>p.PrefabUid==42&&p.EndingEntrance==1&&p.MainEntrance==1);
    }
    [Fact]
    public void NoUniqueSurvivorKeepsIdentityUnknown()
    {
        var s=Scene(allDead:true);var result=new LaneMergeOriginClassifier(s.Graph).Classify(s.Ending,10,s.Main,10);
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);Assert.IsType<MapQueryFailure>(result.MapIssue);
    }
    [Fact]
    public void MissingNativeContinuityIsTypedInsteadOfAssumingTheOnlyPhysicalPathContinues()
    {
        var s=Scene(missing:true,unchanged:true);var result=new LaneMergeOriginClassifier(s.Graph).Classify(s.Ending,10,s.Main,10);
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);
        Assert.Equal(MapQueryFailureKind.DefinitionUnavailable,Assert.IsType<MapQueryFailure>(result.MapIssue).Kind);
    }
    [Fact]
    public void NativeLanesWithoutMergeOrSplitKeepTheirIdentity()
    {
        var s=Scene(unchanged:true);Assert.Equal(LaneMergeKind.LaneReduction,new LaneMergeOriginClassifier(s.Graph).Classify(s.Ending,10,s.Main,10).Kind);
    }
    [Fact]
    public void MergeQualificationAtExactly500StillMattersButBeyondTheWindowDoesNot()
    {
        var inside=Scene(allDead:true,roadLength:480);var outside=Scene(allDead:true,roadLength:481);
        Assert.Equal(LaneMergeKind.Uncertain,new LaneMergeOriginClassifier(inside.Graph).Classify(inside.Ending,10,inside.Main,10).Kind);
        Assert.Equal(LaneMergeKind.LaneReduction,new LaneMergeOriginClassifier(outside.Graph).Classify(outside.Ending,10,outside.Main,10).Kind);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitNewbornRetainsItsKnownCommonInputSourceWithoutClaimingMainQualification(bool noMain)
    {
        Vector3 origin=Vector3.Zero;var std=new NativeLaneCurve(5,new(-10,0,0),origin,new(10,0,0),new(10,0,0),[0,4],[]);
        var left=new NativeLaneCurve(0,origin,new(50,0,noMain?-4:0),new(50,0,noMain?-4:0),new(50,0,noMain?-4:0),[],[5]);
        var right=new NativeLaneCurve(4,origin,new(50,0,4),new(50,0,4),new(50,0,4),[],[5]);
        var geometry=new NativeLaneGeometry(42,[std,left,right]);
        PrefabLanePath Path(int exit,NativeLaneCurve branch)=>new(42,1,100,0,exit,0,exit,1,2,[5,branch.Index],
            [std.Start,origin,branch.End],60){NativeGeometry=geometry,CurveStartPointIndices=[0,1]};
        var a=Path(0,left);var b=Path(1,right);var seed=Scene();
        var graph=new NativeLaneGraph(seed.Graph.Edges.Where(e=>NativeLaneGraph.Prefab(e)!="2A")
            .Append(new DirectedRoad("prefab:2A:1:100",1,100,a.Points){LanePaths=[a,b]}).ToArray());
        var result=new LaneMergeOriginClassifier(graph).Classify(seed.Ending,10,seed.Main,10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);
        Assert.Contains(result.Births,birth=>birth.PrefabUid==42&&birth.BranchCurve==4);
        var split=Assert.Single(LaneContinuity.Analyze(geometry).Points);
        Assert.Equal(LaneContinuityRole.Newborn,split.Branches.Single(v=>v.CurveIndex==4).Role);
    }
    [Fact]
    public void MissingQueriedMappingReturnsTypedUnknownWithoutInventingCoverage()
    {
        var s=Scene();var graph=new NativeLaneGraph(s.Graph.Edges.Where(e=>NativeLaneGraph.Prefab(e)!="63").ToArray());
        var result=new LaneMergeOriginClassifier(graph).Classify(s.Ending,10,s.Main,10);
        Assert.Equal(LaneMergeKind.Uncertain,result.Kind);Assert.IsType<MapQueryFailure>(result.MapIssue);
        Assert.Contains("mapping",result.Reason);
    }
    [Fact]
    public void InvalidUnrelatedNativeCurveDoesNotBlockKnownSourcePair()
    {
        var s=Scene(unchanged:true);var graph=new NativeLaneGraph(s.Graph.Edges.Select(e=>e with
        {LanePaths=e.LanePaths.Select(p=>p.NativeGeometry is {} geometry?p with{NativeGeometry=geometry with
            {Curves=geometry.Curves.Append(new NativeLaneCurve(999,new(0,0,1000),new(1,0,1000),Vector3.UnitX,Vector3.UnitX,[],[])
                {MapIssue=new MapQueryFailure(MapQueryFailureKind.InvalidData,"unrelated broken curve")}).ToArray()}}:p).ToArray()}).ToArray());
        Assert.Equal(LaneMergeKind.LaneReduction,new LaneMergeOriginClassifier(graph).Classify(s.Ending,10,s.Main,10).Kind);
    }
    [Fact]
    public void UnrelatedOutputLaneWithoutNativeDataDoesNotBlockPairOrInventItsApplicability()
    {
        var s=Scene();var edges=s.Graph.Edges.Select(e=>e with{LanePaths=e.LanePaths.Select(p=>p with
            {EntryLaneCount=p.PrefabUid==99||p.EntryNodeUid==1?3:p.EntryLaneCount,ExitLaneCount=p.PrefabUid==42?3:p.ExitLaneCount}).ToArray()}).ToArray();
        var source=edges.Single(e=>e.Id=="prefab:2A:1:100");var extra=source.LanePaths[0] with
            {EntryLane=2,ExitLane=2,EntryOrder=2,ExitOrder=2,NativeGeometry=null,CurveIndices=[999]};
        var seed=edges.Single(e=>NativeLaneGraph.Prefab(e)=="63");var seedExtra=seed.LanePaths[0] with
            {EntryLane=2,EntryOrder=2,NativeGeometry=null,CurveIndices=[999]};
        var graph=new NativeLaneGraph(edges.Select(e=>e==source?e with{LanePaths=e.LanePaths.Append(extra).ToArray()}:
            e==seed?e with{LanePaths=e.LanePaths.Append(seedExtra).ToArray()}:e).ToArray());
        var result=new LaneMergeOriginClassifier(graph).Classify(s.Ending with{EntryLaneCount=3},10,s.Main with{EntryLaneCount=3},10);
        Assert.Equal(LaneMergeKind.LaneReduction,result.Kind);
        Assert.DoesNotContain(2,result.ApplicableEntryLanes);
    }
}
