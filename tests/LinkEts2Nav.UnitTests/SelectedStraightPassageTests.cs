using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;

public sealed class SelectedStraightPassageTests
{
    internal static DirectedRoad[] Fork()
    {
        NativeLaneCurve C(int id,Vector3 a,Vector3 b,int[] next,int[] previous)=>new(id,a,b,b-a,b-a,next,previous);
        var native=NativeLaneFixture.WithInterfaces(42,[C(0,Vector3.Zero,new(0,0,50),[1,2],[]),
            C(1,new(0,0,50),new(0,0,100),[],[0]),C(2,new(0,0,50),new(-50,0,100),[],[0])]);
        DirectedRoad Edge(int last,ulong output)
        {
            var points=new List<Vector3>();var starts=new List<int>();
            foreach(int id in new[]{0,last}){starts.Add(points.Count);points.AddRange(Enumerable.Range(0,17).Select(i=>native.Curves[id].Position(i/16f)));}
            var lane=new PrefabLanePath(42,10,output,0,0,0,0,1,1,[0,last],points,new LanePolyline(points).Length)
                {NativeGeometry=native,CurveStartPointIndices=starts};
            return new($"prefab:2A:10:{output}",10,output,points){LanePaths=[lane],SplitPriority=new(20,[],[],8,"main")};
        }
        return [Edge(1,20),Edge(2,30)];
    }
    [Fact]
    public void SelectedNativeContinuationKeepsItsPhysicalEndAndSameSideReminder()
    {
        var graph=Fork();var actions=new ComponentManeuverPlanner().Build([graph[0]],graph)[0];
        var straight=Assert.Single(actions);Assert.Equal(ManeuverKind.Straight,straight.Kind);
        Assert.Equal(NavigationSide.Right,straight.StraightExitSides);Assert.Equal(100,straight.EndDistanceMeters);
        var target=new NavigationManeuver("target",ManeuverKind.TurnRight,400,"a","b"){JunctionId="next",EndDistanceMeters=450};
        var plan=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,[straight,target],true,false,false,new());
        Assert.Contains(plan.Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
        var environmental=target with{AnnouncementMode=NavigationAnnouncementMode.DistancePreview};
        var env=new NavigationEventPlanner().Build("s","r",DateTimeOffset.UtcNow,0,[straight,environmental],true,false,false,new());
        Assert.DoesNotContain(env.Triggers,t=>t.Kind==SpeechTriggerKind.ActionAssist);
    }
    [Fact]
    public void RepeatedVisitsToTheSameNativePrefabRetainSeparatePhysicalPassages()
    {
        var graph=Fork();var main=graph[0];var loop=new DirectedRoad("loop",20,10,[main.Points[^1],main.Points[0]]);
        var route=new[]{main,loop,main};var result=new ComponentManeuverPlanner().Build(route,[..graph,loop],new LaneRoutePlanner().Build(route));
        var first=Assert.Single(result[0]);var last=Assert.Single(result[2]);
        Assert.Equal(ManeuverKind.Straight,first.Kind);Assert.Equal(ManeuverKind.Straight,last.Kind);
        Assert.Equal(0,first.DistanceMeters);Assert.Equal(100,first.EndDistanceMeters);
        Assert.Equal(200,last.DistanceMeters);Assert.Equal(300,last.EndDistanceMeters);
    }
}
