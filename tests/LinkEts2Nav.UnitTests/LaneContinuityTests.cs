using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneContinuityTests
{
    private static Vector3 Direction(double degrees)
    {
        double r=degrees*Math.PI/180;double x=Math.Cos(r),z=Math.Sin(r);
        return new((float)(Math.Abs(x)<1e-12?0:x),0,(float)(Math.Abs(z)<1e-12?0:z));
    }
    private static NativeLaneGeometry Merge(params double[] angles)
    {
        int shared=angles.Length;
        var branches=angles.Select((angle,i)=>new NativeLaneCurve(i,-Direction(angle)*10,Vector3.Zero,
            Direction(angle)*10,Direction(angle)*10,[shared],[]));
        return new(123,branches.Append(new(shared,Vector3.Zero,new(10,0,0),new(10,0,0),new(10,0,0),[],Enumerable.Range(0,shared).ToArray())).ToArray());
    }
    private static NativeLaneGeometry Split(params double[] angles)
    {
        int shared=angles.Length;
        var branches=angles.Select((angle,i)=>new NativeLaneCurve(i,Vector3.Zero,Direction(angle)*10,
            Direction(angle)*10,Direction(angle)*10,[],[shared]));
        return new(123,branches.Append(new(shared,new(-10,0,0),Vector3.Zero,new(10,0,0),new(10,0,0),Enumerable.Range(0,shared).ToArray(),[])).ToArray());
    }
    private static LaneContinuityPoint Point(NativeLaneGeometry geometry)=>Assert.Single(LaneContinuity.Analyze(geometry).Points);

    [Fact]
    public void AllNCandidatesAreComparedTogetherAndOnlyOneCanSurvive()
    {
        var point=Point(Merge(10,30,60,120));
        Assert.Equal(LaneContinuityRole.Survivor,point.Branches[0].Role);
        Assert.All(point.Branches.Skip(1),b=>Assert.Equal(LaneContinuityRole.Dead,b.Role));
        var tied=Point(Merge(10,15,60));
        Assert.All(tied.Branches,b=>Assert.Equal(LaneContinuityRole.Dead,b.Role));
    }
    [Fact]
    public void StrictRatioBoundaryTwoIsExcluded()
    {
        var point=Point(Merge(45,90,135));
        Assert.Equal(2,point.Branches[1].NormalizedAngle!.Value,8);
        Assert.Equal(LaneContinuityRole.Survivor,point.Branches[0].Role);
        Assert.Equal(LaneContinuityRole.Dead,point.Branches[1].Role);
    }
    [Fact]
    public void OneZeroSurvivesButSeveralZeroCandidatesAllLoseQualification()
    {
        var one=Point(Merge(0,10,30));
        Assert.Equal(LaneContinuityRole.Survivor,one.Branches[0].Role);
        Assert.All(one.Branches.Skip(1),b=>Assert.Equal(LaneContinuityRole.Dead,b.Role));
        var multiple=Point(Merge(0,0,20));
        Assert.All(multiple.Branches,b=>Assert.Equal(LaneContinuityRole.Dead,b.Role));
        Assert.Null(multiple.Branches[2].NormalizedAngle); // unbounded relative to a zero minimum
        Assert.Equal(LaneTurnKind.Unknown,multiple.Branches[0].Turn); // direction uncertainty does not erase a known death
    }
    [Fact]
    public void SplitUsesTheSameQualificationButReportsActualTravelTurnDirection()
    {
        var merge=Point(Merge(0,45));var split=Point(Split(0,45));
        Assert.Equal(LaneContinuityRole.Newborn,split.Branches[1].Role);
        Assert.Equal(LaneTurnKind.ForwardRight,split.Branches[1].Turn);
        Assert.Equal(LaneTurnKind.ForwardLeft,merge.Branches[1].Turn);
        Assert.Equal(Vector3.Zero,split.Origin);Assert.Equal(Vector3.Zero,merge.Origin);
    }
    [Theory]
    [InlineData(-44,LaneTurnKind.ForwardRight)]
    [InlineData(-46,LaneTurnKind.Right)]
    [InlineData(-134,LaneTurnKind.Right)]
    [InlineData(-136,LaneTurnKind.RearRight)]
    [InlineData(44,LaneTurnKind.ForwardLeft)]
    [InlineData(46,LaneTurnKind.Left)]
    [InlineData(136,LaneTurnKind.RearLeft)]
    public void NativeStartTangentDeterminesDeathTurnVocabulary(double angle,LaneTurnKind expected)
        =>Assert.Equal(expected,Point(Merge(0,angle)).Branches[1].Turn);
    [Theory]
    [InlineData(false,false,LaneTurnKind.ForwardRight)]
    [InlineData(false,true,LaneTurnKind.ForwardLeft)]
    [InlineData(true,false,LaneTurnKind.RearRight)]
    [InlineData(true,true,LaneTurnKind.RearLeft)]
    public void NearCollinearAndBackwardsTurnsUseOriginRelativeToTheStartRay(bool backward,bool left,LaneTurnKind expected)
    {
        var geometry=Merge(0,backward?180:0);var branch=geometry.Curves[1];
        var start=branch.Start+new Vector3(0,0,(left?1:-1)*(backward?-1:1)*3);
        geometry=geometry with{Curves=[geometry.Curves[0],branch with{Start=start},geometry.Curves[2]]};
        Assert.Equal(expected,Point(geometry).Branches[1].Turn);
    }
    [Fact]
    public void CandidateUsesItsNativeMidpointTangentRatherThanStartOrWholePrefabChord()
    {
        var geometry=Merge(0,50);var curve=geometry.Curves[1];
        // Keep the midpoint unchanged while changing both endpoint derivatives equally and oppositely.
        var change=new Vector3(0,0,50);
        geometry=geometry with{Curves=[geometry.Curves[0],curve with{StartTangent=curve.StartTangent+change,EndTangent=curve.EndTangent-change},geometry.Curves[2]]};
        Assert.Equal(50,Point(geometry).Branches[1].AngleDegrees!.Value,4);
    }
    [Fact]
    public void MissingMidpointOrStandardGeometryMakesTheWholeLocalChoiceUnknown()
    {
        var geometry=Merge(0,30);var branch=geometry.Curves[1] with{Start=Vector3.Zero,End=Vector3.Zero,StartTangent=Vector3.UnitX,EndTangent=-Vector3.UnitX};
        var unknown=Point(geometry with{Curves=[geometry.Curves[0],branch,geometry.Curves[2]]});
        Assert.NotNull(unknown.MapIssue);Assert.All(unknown.Branches,b=>Assert.Equal(LaneContinuityRole.Unknown,b.Role));
        var standard=geometry.Curves[2] with{StartTangent=Vector3.Zero};
        Assert.All(Point(geometry with{Curves=[geometry.Curves[0],geometry.Curves[1],standard]}).Branches,b=>Assert.Equal(LaneContinuityRole.Unknown,b.Role));
    }
    [Fact]
    public void SeparateNativeMergePointsRemainSeparateAndUnconnectedLanesSurvive()
    {
        var a=Merge(0,30);var b=Merge(0,-30).Curves.Select(c=>c with{Index=c.Index+10,Next=c.Next.Select(i=>i+10).ToArray(),Previous=c.Previous.Select(i=>i+10).ToArray()});
        var independent=new NativeLaneCurve(50,new(0,0,4),new(10,0,4),Vector3.UnitX,Vector3.UnitX,[],[]);
        var result=LaneContinuity.Analyze(new(123,a.Curves.Concat(b).Append(independent).ToArray()));
        Assert.Equal(new[]{2,12},result.Points.Select(p=>p.SharedCurveIndex));
        Assert.Contains(50,result.UnchangedCurveIndices);
        Assert.All(result.Points,p=>Assert.Single(p.Branches,b=>b.Role==LaneContinuityRole.Survivor));
    }
    [Fact]
    public void PlainCurvesDoNotRequireAnyJunctionClassification()
    {
        var curve=new NativeLaneCurve(0,Vector3.Zero,Vector3.UnitX,Vector3.UnitX,Vector3.UnitX,[],[]);
        var result=LaneContinuity.Analyze(new(1,[curve]));
        Assert.Empty(result.Points);Assert.Equal(new[]{0},result.UnchangedCurveIndices);
    }
    [Fact]
    public void MissingOrNonreciprocalNativeReferencesAreUnknownRatherThanInventedLinks()
    {
        var geometry=Merge(0,20);var broken=geometry.Curves[1] with{Next=[]};
        Assert.All(Point(geometry with{Curves=[geometry.Curves[0],broken,geometry.Curves[2]]}).Branches,b=>Assert.Equal(LaneContinuityRole.Unknown,b.Role));
        var omitted=geometry.Curves[2] with{Previous=[]};
        Assert.All(Point(geometry with{Curves=[geometry.Curves[0],geometry.Curves[1],omitted]}).Branches,b=>Assert.Equal(LaneContinuityRole.Unknown,b.Role));
        Assert.NotNull(LaneContinuity.Analyze(geometry with{Curves=geometry.Curves.Append(geometry.Curves[0]).ToArray()}).MapIssue);
    }
    [Fact]
    public void HermiteTangentsAreTranslationInvariantEvenWithRepeatedControlPositions()
    {
        var curve=new NativeLaneCurve(0,new(1,0,1),new(1,0,1),new(4,0,2),new(1,0,3),[],[]);
        var shift=new Vector3(100000,400,-150000);var moved=curve with{Start=curve.Start+shift,End=curve.End+shift};
        Assert.Equal(curve.Tangent(.5f),moved.Tangent(.5f));
        Assert.Equal(curve.StartTangent,curve.Tangent(0));Assert.Equal(curve.EndTangent,curve.Tangent(1));
    }
    [Fact]
    public void ProductionGeometryPreservesCurveIndicesAndExcludesDisallowedTracks()
    {
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,-MathF.PI/2);
        var descriptor=new PrefabDescriptor
        {
            Nodes=[new ControlNode{Position=new(-10,0,-4),InputLines=[0],OutputLines=[]},
                new ControlNode{Position=new(-10,0,4),InputLines=[1],OutputLines=[]},
                new ControlNode{Position=new(10,0,0),InputLines=[],OutputLines=[2]}],
            NavCurves=[new NavCurve{StartPosition=new(-10,0,-4),EndPosition=Vector3.Zero,Length=11,StartRotation=rotation,EndRotation=rotation,NextLines={2}},
                new NavCurve{StartPosition=new(-10,0,4),EndPosition=Vector3.Zero,Length=11,StartRotation=rotation,EndRotation=rotation,NextLines={2},TrafficRule="tram"},
                new NavCurve{StartPosition=Vector3.Zero,EndPosition=new(10,0,0),Length=10,StartRotation=rotation,EndRotation=rotation,PreviousLines={0,1}}]
        };
        var prefab=new Prefab{Uid=9,Nodes=descriptor.Nodes.Select((n,i)=>(INode)new Node{Uid=(ulong)(10+i),Position=n.Position}).ToList()};
        var projected=PrefabPlayerAccess.Build(descriptor,[null,null,null],[null,null,null],rule=>rule=="tram"?false:null);
        var edges=PrefabPathGeometry.Build(prefab,projected.Descriptor);
        var path=Assert.Single(Assert.Single(edges).LanePaths);var geometry=Assert.IsType<NativeLaneGeometry>(path.NativeGeometry);
        Assert.Equal(new[]{0,2},geometry.Curves.Select(c=>c.Index));
        Assert.Equal(new[]{0},geometry.Curves.Single(c=>c.Index==2).Previous);
        Assert.Empty(LaneContinuity.Analyze(geometry).Points);
        Assert.All(geometry.Curves,c=>Assert.True(c.StartTangent.Length()>1));
    }
}
