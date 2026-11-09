using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class LaneNavigationTests
{
    private static PrefabLanePath Lane(ulong entry,ulong exit,int from,int to,int curve,double length=20)
        => new(1,entry,exit,from,to,from,to,3,3,[curve],[new(from,0,0),new(to,0,20)],length);
    private static DirectedRoad Edge(string id,ulong entry,ulong exit,params PrefabLanePath[] paths)
        => new(id,entry,exit,[new(0,0,0),new(0,0,20)]){LanePaths=paths};
    [Fact]
    public void SelectsWholeRouteToKeepTheRequiredLaneRatherThanShortestLocalChoice()
    {
        var first=Edge("prefab:A:0:1",10,20,Lane(10,20,0,0,1,10),Lane(10,20,0,2,2,30));
        var road=Edge("road",20,30);
        var second=Edge("prefab:B:0:1",30,40,Lane(30,40,2,1,7));
        var plan=new LaneRoutePlanner().Build([first,road,second]);
        Assert.True(plan.Complete);Assert.Equal(2,plan.Sections[0].Path.ExitLane);
        Assert.Equal(7,Assert.Single(plan.Sections[1].Path.CurveIndices));
        Assert.True(Assert.Single(plan.Transfers).SameLaneVerified);
        Assert.Equal("road",Assert.Single(plan.Transfers[0].RoadIds));
    }
    [Fact]
    public void LaneChangeIsExplicitAndNotClaimedToBeVerified()
    {
        var plan=new LaneRoutePlanner().Build([Edge("prefab:A:0:1",10,20,Lane(10,20,0,0,1)),
            Edge("road",20,30),Edge("prefab:B:0:1",30,40,Lane(30,40,2,1,7))]);
        Assert.False(plan.Complete);Assert.False(Assert.Single(plan.Transfers).SameLaneVerified);
        Assert.Contains("变道",plan.Failure);
        Assert.Equal(2,plan.Sections.Count);
    }
    [Fact]
    public void AdjacentPrefabsCannotTeleportAcrossLanes()
    {
        var plan=new LaneRoutePlanner().Build([Edge("prefab:A:0:1",10,20,Lane(10,20,0,0,1)),
            Edge("prefab:B:0:1",20,30,Lane(20,30,2,1,7))]);
        Assert.False(plan.Complete);Assert.Empty(plan.Sections);
    }
    [Fact]
    public void MissingLaneGraphAndWrongOfficialBoundaryAreNotInvented()
    {
        Assert.False(new LaneRoutePlanner().Build([Edge("prefab:A:0:1",10,20)]).Complete);
        Assert.False(new LaneRoutePlanner().Build([Edge("prefab:A:0:1",10,20,Lane(10,99,0,0,1))]).Complete);
    }
    [Fact]
    public void BuildsAllLaneChainsWithExactNativeCurveIndices()
    {
        var map=new TruckLib.ScsMap.Map();var a=map.AddNode(Vector3.Zero,true);var b=map.AddNode(new(0,0,-40),false);
        var prefab=new Prefab{Uid=123,Nodes=[a,b]};
        var descriptor=new PrefabDescriptor {Nodes=[new ControlNode{Position=Vector3.Zero,InputLines=[0,1],OutputLines=[]},
            new ControlNode{Position=new(0,0,-40),InputLines=[],OutputLines=[2,3]}],NavCurves=[
            Curve(-2,0,-20,2),Curve(2,0,-20,3),Curve(-2,-20,-40),Curve(2,-20,-40)]};
        var path=Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.Equal(2,path.LanePaths.Count);
        Assert.Contains(path.LanePaths,p=>p.CurveIndices.SequenceEqual(new[]{0,2})&&p.EntryLane==0&&p.ExitLane==0);
        Assert.Contains(path.LanePaths,p=>p.CurveIndices.SequenceEqual(new[]{1,3})&&p.EntryLane==1&&p.ExitLane==1);
        Assert.All(path.LanePaths,p=>{Assert.Equal(40,p.LengthMeters,2);Assert.Equal(a.Uid,p.EntryNodeUid);Assert.Equal(b.Uid,p.ExitNodeUid);});
        prefab.ChangeOrigin(1);
        var rotated=Assert.Single(PrefabPathGeometry.Build(prefab,descriptor));
        Assert.Equal(2,rotated.LanePaths.Count);
        Assert.All(rotated.LanePaths,p=>{Assert.Equal(a.Uid,p.EntryNodeUid);Assert.Equal(b.Uid,p.ExitNodeUid);});
    }
    private static NavCurve Curve(float x,float start,float end,int? next=null)
    {
        var c=new NavCurve{StartPosition=new(x,0,start),EndPosition=new(x,0,end),Length=Math.Abs(end-start),
            StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity};
        if(next.HasValue)c.NextLines.Add(next.Value);return c;
    }
}
