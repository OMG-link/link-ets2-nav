using System.Numerics;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.UnitTests;

public sealed class RoundaboutBoundaryTests
{
    private static (Dictionary<RoundaboutCurveKey,RoundaboutCurve> Graph,RoundaboutCurveKey[] First,RoundaboutCurveKey[] Other) TwoRings()
    {
        var graph=new Dictionary<RoundaboutCurveKey,RoundaboutCurve>();
        RoundaboutCurveKey[] AddRing(int start,float radius,int[] arms)
        {
            Vector3 P(int step,float r)=>new(r*MathF.Cos(step*MathF.PI/4),0,-r*MathF.Sin(step*MathF.PI/4));
            var keys=Enumerable.Range(start,8).Select(i=>new RoundaboutCurveKey(1,i)).ToArray();
            for(int i=0;i<8;i++)
            {
                var next=new List<RoundaboutCurveKey>{keys[(i+1)%8]};
                if(arms.Contains((i+1)%8))next.Add(new(1,start+100+(i+1)%8));
                graph[keys[i]]=new(keys[i],1,false,[P(i,radius),P(i+1,radius)],next);
            }
            foreach(int arm in arms)
            {
                ulong uid=(ulong)(10+arm);
                var input=new RoundaboutCurveKey(1,start+200+arm);var output=new RoundaboutCurveKey(1,start+100+arm);
                graph[input]=new(input,1,false,[P(arm,40),P(arm,radius)],[keys[arm]],uid);
                graph[output]=new(output,1,false,[P(arm,radius),P(arm,40)],[],0,uid);
            }
            return keys;
        }
        var first=AddRing(0,10,[0,4]);var other=AddRing(1000,20,[0,2,4]);
        return (graph,first,other);
    }

    [Fact]
    public void AllNativeBoundaryInterfacesExistWithoutAPathToTheRepresentativeRing()
    {
        var (graph,first,other)=TwoRings();
        var a=RoundaboutPorts.Build(graph,first,[1],TestContext.Current.CancellationToken);
        var b=RoundaboutPorts.Build(graph,other,[1],TestContext.Current.CancellationToken);
        Assert.Equal(new ulong[]{10,12,14},a.Entries.Select(p=>p.NodeUid));
        Assert.Equal(new ulong[]{10,12,14},a.Exits.Select(p=>p.BoundaryNodeUid));
        foreach(var pair in a.Entries.Zip(b.Entries))Assert.Equal(pair.First.Bearing,pair.Second.Bearing,6);
        foreach(var pair in a.Exits.Zip(b.Exits))Assert.Equal(pair.First.Bearing,pair.Second.Bearing,6);
        Assert.Equal(2,a.Exits.Single(p=>p.BoundaryNodeUid==10).Branches.Count);
        Assert.All(a.Exits,p=>Assert.NotEmpty(p.DepartureCandidates));
        Assert.Contains(new Vector3(0,0,-20),a.Exits.Single(p=>p.BoundaryNodeUid==12).DepartureCandidates,
            new NearPointComparer());
    }

    [Fact]
    public void OnlyInputAndOnlyOutputInterfacesStayDirectionalAndInternalMemberNodesDisappear()
    {
        var (graph,ring,_)=TwoRings();
        graph[new(1,3000)]=new(new(1,3000),1,false,[new(40,0,40),new(10,0,0)],[ring[0]],90);
        graph[new(1,3001)]=new(new(1,3001),1,false,[new(10,0,0),new(50,0,0)],[new(2,0)],0,100);
        graph[new(2,0)]=new(new(2,0),2,false,[new(50,0,0),new(60,0,0)],[],100,101);
        var ports=RoundaboutPorts.Build(graph,ring,[1,2],TestContext.Current.CancellationToken);
        Assert.Contains(ports.Entries,p=>p.NodeUid==90);
        Assert.DoesNotContain(ports.Exits,p=>p.BoundaryNodeUid==90);
        Assert.Contains(ports.Exits,p=>p.BoundaryNodeUid==101);
        Assert.DoesNotContain(ports.Entries,p=>p.NodeUid==101);
        Assert.DoesNotContain(ports.Entries,p=>p.NodeUid==100);
        Assert.DoesNotContain(ports.Exits,p=>p.BoundaryNodeUid==100);
    }

    [Fact]
    public void GeometricReverseRoadDoesNotCreateAFictitiousTrafficDirection()
    {
        var (graph,ring,_)=TwoRings();
        graph[new(9,-1)]=new(new(9,-1),null,false,[new(60,0,0),new(80,0,0)],[],101,102){ProvenTraffic=true};
        graph[new(9,-2)]=new(new(9,-2),null,false,[new(80,0,0),new(60,0,0)],[],102,101){ProvenTraffic=false};
        var ports=RoundaboutPorts.Build(graph,[..ring,new(9,-1)],[1],TestContext.Current.CancellationToken);
        Assert.Contains(ports.Exits,p=>p.BoundaryNodeUid==102);
        Assert.DoesNotContain(ports.Entries,p=>p.NodeUid==102);
    }

    private sealed class NearPointComparer:IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a,Vector3 b)=>Vector3.DistanceSquared(a,b)<1e-6;
        public int GetHashCode(Vector3 value)=>0;
    }
}
