using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneContinuityBlinkerTests
{
    private static NativeLaneCurve C(int id,Vector3 start,Vector3 end,int[] next,int[] previous,
        LaneBlinker lamp=LaneBlinker.NoBlinker)=>new(id,start,end,end-start,end-start,next,previous){Blinker=lamp};
    private static NativeLaneGeometry Merge()=>NativeLaneFixture.WithInterfaces(42,[
        C(0,new(-100,0,0),Vector3.Zero,[2],[]) with{StartTangent=new(100,0,3),EndTangent=new(100,0,-3)},
        C(1,new(-100,0,15),Vector3.Zero,[2],[]),C(2,Vector3.Zero,new(100,0,0),[],[0,1])]);
    private static NativeLaneGeometry Edit(NativeLaneGeometry g,int id,Func<NativeLaneCurve,NativeLaneCurve> edit)
        =>NativeLaneFixture.WithInterfaces(g.PrefabUid,g.Curves.Select(c=>c.Index==id?edit(c):c).ToArray());
    private static LaneContinuityPoint Point(NativeLaneGeometry g)=>Assert.Single(LaneContinuity.Analyze(g).Points,
        p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==2);
    private static LaneContinuityBranch Branch(LaneContinuityPoint p,int id)=>Assert.Single(p.Branches,b=>b.CurveIndex==id);
    private static NativeLaneGeometry Predecessors(NativeLaneGeometry g,params NativeLaneCurve[] curves)
        =>NativeLaneFixture.WithInterfaces(g.PrefabUid,[..g.Curves,..curves]);

    [Theory]
    [InlineData(LaneBlinker.Left)]
    [InlineData(LaneBlinker.Right)]
    public void SmallestAngleIsVetoedBeforeNormalizingRemainingCandidate(LaneBlinker lamp)
    {
        var result=Point(Edit(Merge(),0,c=>c with{Blinker=lamp}));
        var dead=Branch(result,0);var alive=Branch(result,1);
        Assert.Equal(LaneContinuityRole.Dead,dead.Role);Assert.True(dead.VetoedByBlinker);
        Assert.Equal(0,dead.BlinkerSourceCurveIndex);Assert.Null(dead.AngleDegrees);Assert.Null(dead.NormalizedAngle);
        Assert.Equal(LaneTurnKind.ForwardLeft,dead.Turn);
        Assert.Equal(LaneContinuityRole.Survivor,alive.Role);Assert.Equal(1,alive.NormalizedAngle);
        Assert.True(alive.AngleDegrees>8);
    }
    [Fact]
    public void VetoNeverEvaluatesDegenerateMidpointButStillDescribesItsTurn()
    {
        var g=Edit(Merge(),0,c=>c with{Blinker=LaneBlinker.Right,StartTangent=new(300,0,3),EndTangent=new(300,0,-3)});
        Assert.Equal(Vector3.Zero,g.Curves[0].Tangent(.5f));
        var point=Point(g);Assert.Null(point.MapIssue);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(point,1).Role);
        Assert.Equal(LaneTurnKind.ForwardLeft,Branch(point,0).Turn);
        Assert.Null(Branch(point,0).AngleDegrees);Assert.Null(Branch(point,0).NormalizedAngle);
    }
    [Fact]
    public void EveryDirectionalCandidateDiesWithoutInventingAMinimum()
    {
        var g=Edit(Edit(Merge(),0,c=>c with{Blinker=LaneBlinker.Left}),1,c=>c with{Blinker=LaneBlinker.Right});
        Assert.All(Point(g).Branches,b=>{Assert.Equal(LaneContinuityRole.Dead,b.Role);Assert.True(b.VetoedByBlinker);Assert.Null(b.AngleDegrees);Assert.Null(b.NormalizedAngle);});
    }
    [Theory]
    [InlineData(20,false)]
    [InlineData(35,true)]
    public void MultipleRemainingCandidatesUseTheirOwnMinimumAndOriginalRatio(float offset,bool unique)
    {
        var g=Edit(Edit(Merge(),0,c=>c with{Blinker=LaneBlinker.Right}),2,c=>c with{Previous=[0,1,3]});
        g=Predecessors(g,C(3,new(-100,0,offset),Vector3.Zero,[2],[]));
        var point=Point(g);
        Assert.Equal(unique?LaneContinuityRole.Survivor:LaneContinuityRole.Dead,Branch(point,1).Role);
        Assert.Equal(LaneContinuityRole.Dead,Branch(point,3).Role);Assert.Equal(1,Branch(point,1).NormalizedAngle);
        Assert.Equal(unique,Branch(point,3).NormalizedAngle>=2);
    }
    [Fact]
    public void ForcedNoBlinkerRemainsAnOrdinaryCandidate()
    {
        var point=Point(Edit(Merge(),0,c=>c with{Blinker=LaneBlinker.NoBlinkerForced}));
        Assert.Equal(LaneContinuityRole.Survivor,Branch(point,0).Role);Assert.False(Branch(point,0).VetoedByBlinker);
    }
#if DEBUG
    [Fact]
    public void InternalMergeGeometryRequiresAKnownBlinker()
    {
        var geometry=Edit(Merge(),0,c=>c with{Blinker=(LaneBlinker)3});
        Assert.Throws<NavigationContractException>(()=>Point(geometry));
    }
#endif
    [Fact]
    public void ConfirmedDirectionalPredecessorCanVetoAnUnmarkedCurve()
    {
        var g=Edit(Merge(),0,c=>c with{Previous=[5]});
        g=Predecessors(g,C(5,new(-200,0,0),new(-100,0,0),[0],[],LaneBlinker.Right));
        var point=Point(g);Assert.Null(point.MapIssue);Assert.Equal(5,Branch(point,0).BlinkerSourceCurveIndex);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(point,1).Role);
    }
#if DEBUG
    [Fact]
    public void MissingPredecessorViolatesNativeGeometryContract()
    {
        var geometry=Edit(Merge(),0,c=>c with{Previous=[99]});
        Assert.Throws<NavigationContractException>(()=>Point(geometry));
    }
#endif
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviousMergeStopsOlderMarkersButIncludesMarkerOnItsOutgoingCurve(bool markerAfterOrigin)
    {
        var g=Edit(Merge(),0,c=>c with{Previous=[5,6],Blinker=markerAfterOrigin?LaneBlinker.Right:LaneBlinker.NoBlinker});
        g=Predecessors(g,C(5,new(-200,0,0),new(-100,0,0),[0],[],LaneBlinker.Left),C(6,new(-200,0,10),new(-100,0,0),[0],[]));
        var branch=Branch(Point(g),0);Assert.Equal(markerAfterOrigin,branch.VetoedByBlinker);
        Assert.Equal(markerAfterOrigin?LaneContinuityRole.Dead:LaneContinuityRole.Survivor,branch.Role);
    }
    [Fact]
    public void PreviousSplitStopsBeforeReadingItsIncomingCurvesMarker()
    {
        var g=Edit(Merge(),0,c=>c with{Previous=[5]});
        g=Predecessors(g,C(5,new(-200,0,0),new(-100,0,0),[0,6],[],LaneBlinker.Right),C(6,new(-100,0,0),new(-50,0,30),[],[5]));
        Assert.Equal(LaneContinuityRole.Survivor,Branch(Point(g),0).Role);Assert.False(Branch(Point(g),0).VetoedByBlinker);
    }
#if DEBUG
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteInterveningSplitViolatesNativeGeometryContract(bool missingCurve)
    {
        var g=Edit(Merge(),0,c=>c with{Previous=[5]});
        g=Predecessors(g,C(5,new(-200,0,0),new(-100,0,0),missingCurve?[0,99]:[0],[]));
        if(!missingCurve)g=Predecessors(g,C(6,new(-100,0,0),new(-50,0,30),[],[5]));
        Assert.Throws<NavigationContractException>(()=>Point(g));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteInterveningMergeViolatesNativeGeometryContract(bool missingCurve)
    {
        var g=Edit(Merge(),0,c=>c with{Previous=missingCurve?[5,99]:[5]});
        g=Predecessors(g,C(5,new(-200,0,0),new(-100,0,0),[0],[]));
        if(!missingCurve)g=Predecessors(g,C(6,new(-200,0,10),new(-100,0,0),[0],[]));
        Assert.Throws<NavigationContractException>(()=>Point(g));
    }
#endif
    [Fact]
    public void ARemainingCandidateStillNeedsItsMidpointGeometry()
    {
        var g=Edit(Edit(Merge(),0,c=>c with{Blinker=LaneBlinker.Right}),1,c=>c with{StartTangent=3*(c.End-c.Start),EndTangent=3*(c.End-c.Start)});
        Assert.NotNull(Point(g).MapIssue);
    }
    [Fact]
    public void RealGermanPredecessorVetoLeavesTheIndependentSplitUntouched()
    {
        var geometry=NativeBlinkerFixtures.GermanIndependentGroups();
        var result=LaneContinuity.Analyze(geometry);
        var merge=Assert.Single(result.Points,p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==3);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(merge,4).Role);
        Assert.Equal(LaneContinuityRole.Dead,Branch(merge,5).Role);
        Assert.Equal(8,Branch(merge,5).BlinkerSourceCurveIndex);
        Assert.Equal(LaneTurnKind.ForwardRight,Branch(merge,5).Turn);
        Assert.Equal(0,Branch(merge,5).TurnSourceCurveIndex);
        var unmarked=LaneContinuity.Analyze(geometry with{Curves=geometry.Curves.Select(c=>c with{Blinker=LaneBlinker.NoBlinker}).ToArray()});
        var split=Assert.Single(result.Points,p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==6);
        var other=Assert.Single(unmarked.Points,p=>p.Kind==LaneContinuityPointKind.Split&&p.SharedCurveIndex==6);
        Assert.Equal(other.Branches.ToArray(),split.Branches.ToArray());Assert.Equal(other.StandardTangent,split.StandardTangent);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(split,1).Role);
        Assert.Equal(LaneContinuityRole.Newborn,Branch(split,2).Role);
    }
    [Fact]
    public void RealUniversalJoinVetoesThePreviouslyStraightestRightBlinker()
    {
        var geometry=NativeBlinkerFixtures.UniversalJoin();
        var unmarked=LaneContinuity.Analyze(geometry with{Curves=geometry.Curves.Select(c=>c with{Blinker=LaneBlinker.NoBlinker}).ToArray()});
        var old=Assert.Single(unmarked.Points,p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==4);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(old,3).Role);
        var actual=Assert.Single(LaneContinuity.Analyze(geometry).Points,p=>p.Kind==LaneContinuityPointKind.Merge&&p.SharedCurveIndex==4);
        Assert.Equal(3,Branch(actual,3).BlinkerSourceCurveIndex);Assert.Null(Branch(actual,3).AngleDegrees);
        Assert.Equal(LaneContinuityRole.Dead,Branch(actual,3).Role);
        Assert.Equal(LaneContinuityRole.Survivor,Branch(actual,10).Role);Assert.Equal(1,Branch(actual,10).NormalizedAngle);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void NativeProjectionRetainsTheExactBlinkerValue(int raw)
    {
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2);
        var descriptor=new TruckLib.Models.Ppd.PrefabDescriptor
        {
            Nodes=[new(){Position=Vector3.Zero,InputLines=[0,-1],OutputLines=[]},new(){Position=new(100,0,0),InputLines=[],OutputLines=[0,-1]}],
            NavCurves=[new(){StartPosition=Vector3.Zero,EndPosition=new(100,0,0),StartRotation=rotation,EndRotation=rotation,Length=100,
                Blinker=(TruckLib.Models.Ppd.BlinkerType)raw}]
        };
        var prefab=new TruckLib.ScsMap.Prefab{Uid=42,Nodes=[new TruckLib.ScsMap.Node{Uid=10,Position=Vector3.Zero},new TruckLib.ScsMap.Node{Uid=20,Position=new(100,0,0)}]};
        var path=Assert.Single(Assert.Single(PrefabPathGeometry.Build(prefab,descriptor)).LanePaths);
        Assert.Equal(raw,(int)Assert.Single(path.NativeGeometry!.Curves).Blinker);
        Assert.Equal([0],path.NativeGeometry.InputCurveIndices);Assert.Equal([0],path.NativeGeometry.OutputCurveIndices);
    }
}
