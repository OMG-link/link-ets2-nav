using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class SpecialRoundaboutCatalogTests
{
    private static RoundaboutCurve[] Circle()=>Enumerable.Range(0,16).Select(i=>new RoundaboutCurve(new(1,i),1,false,
        [P(i),P(i+.5),P(i+1)],i==15?[]:[new(1,i+1)])).ToArray();
    private static Vector3 P(double i)=>new((float)(20*Math.Cos(i*Math.Tau/16)),0,(float)(-20*Math.Sin(i*Math.Tau/16)));
    private static SpecialRoundaboutCatalog Catalog(RoundaboutCurve[] graph)=>new([new("test","Reviewed reference",
        [new("body","/test.ppd",null,16,2)],graph.Select(c=>new SpecialRoundaboutArc("body",c.Key.CurveIndex,c.Next.Select(k=>k.CurveIndex).ToArray())).ToArray())]);
    [Fact]
    public void EmbeddedCatalogContainsReviewedHelsinkiDefinition()
    {
        var d=Assert.Single(SpecialRoundaboutCatalog.Default.Definitions);
        Assert.Equal("helsinki-port-tram-roundabout",d.Id);
        Assert.Equal(41,Assert.Single(d.Members).CurveCount);
        Assert.Equal(new[]{30,31,26,32,12,34,36},d.ReferenceCircuit.Select(a=>a.Curve));
    }
    [Fact]
    public void ExplicitReferenceRecognizesNonNativeCircuitWithoutChangingDrivingGraph()
    {
        var graph=Circle();var original=graph[^1];var catalog=Catalog(graph);
        Assert.Null(new RoundaboutBuilder(graph).Find(1,[graph[0].Key],TestContext.Current.CancellationToken));
        var result=Assert.Single(catalog.Build(graph.ToDictionary(c=>c.Key),new Dictionary<ulong,SpecialRoundaboutPrefab>{{1,new("/test.ppd",16,2)}},new HashSet<ulong>(),token:TestContext.Current.CancellationToken));
        Assert.Equal("test",result.SpecialDefinitionId);
        Assert.Equal(new ulong[]{1},result.PrefabUids);
        Assert.Empty(original.Next);Assert.Same(original,graph[^1]);
    }
    [Theory]
    [InlineData("/different.ppd",16,false)]
    [InlineData("/test.ppd",17,false)]
    [InlineData("/test.ppd",16,true)]
    public void WrongResourceVersionOrNativeConnectivityDoesNotApply(string path,int count,bool changed)
    {
        var graph=Circle();var catalog=Catalog(graph);
        if(changed)graph[3]=graph[3] with{Next=[]};
        Assert.Empty(catalog.Build(graph.ToDictionary(c=>c.Key),new Dictionary<ulong,SpecialRoundaboutPrefab>{{1,new(path,count,2)}},new HashSet<ulong>(),token:TestContext.Current.CancellationToken));
    }
    [Fact]
    public void CombinationRequiresAllMembersAndCanCompleteAfterSectorWasAlreadyBuilt()
    {
        var catalog=new SpecialRoundaboutCatalog([new("pair","Two prefabs",
            [new("a","/a.ppd","1",1,2),new("b","/b.ppd","2",1,2)],
            [new("a",0,[]),new("b",0,[])])]);
        var left=new Node{Uid=10,Position=new(-10,0,0)};var right=new Node{Uid=20,Position=new(10,0,0)};
        PrefabDescriptor Half(Node start,Node end,float startYaw,float endYaw)=>new()
        {
            Nodes=[new ControlNode{Position=start.Position,InputLines=[0],OutputLines=[]},new ControlNode{Position=end.Position,InputLines=[],OutputLines=[0]}],
            NavCurves=[new NavCurve{StartPosition=start.Position,EndPosition=end.Position,StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,startYaw),EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,endYaw),Length=10*MathF.PI}]
        };
        var index=new RoundaboutMapIndex(catalog);var sector=TruckLib.ScsMap.Map.GetSectorOfCoordinate(left.Position);
        index.AddPrefab(new Prefab{Uid=1,Nodes=[left,right]},Half(left,right,MathF.PI,0),"/a.ppd");
        index.BuildNewSectors([sector],TestContext.Current.CancellationToken);Assert.Empty(index.Structures);
        index.AddPrefab(new Prefab{Uid=2,Nodes=[right,left]},Half(right,left,0,MathF.PI),"/b.ppd");
        index.BuildNewSectors([sector],TestContext.Current.CancellationToken);
        var result=Assert.Single(index.Structures);Assert.Equal("pair",result.SpecialDefinitionId);
        Assert.Equal(new ulong[]{1,2},result.PrefabUids);Assert.Same(result,index.RoadAnnotations[2]);
    }
    [Fact]
    public void AmbiguousCombinationWithoutExplicitInstanceIdsIsRejected()
    {
        Assert.Throws<InvalidDataException>(()=>new SpecialRoundaboutCatalog([new("bad","Ambiguous",
            [new("a","/a.ppd",null,1,2),new("b","/b.ppd",null,1,2)],
            [new("a",0,[]),new("b",0,[])])]));
    }
}
