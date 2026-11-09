using System.Numerics;
using LinkEts2Nav.Map;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class RoundaboutBuilderTests
{
    private static RoundaboutCurve[] Circle(int owners=1,bool left=false)
        => Enumerable.Range(0,32).Select(i=>new RoundaboutCurve(new((ulong)(1+i*owners/32),i),
            (ulong)(1+i*owners/32),left,
            Enumerable.Range(0,5).Select(j=>Point((left?1:-1)*(i+j/4d)*Math.PI/16)).ToArray(),
            [new((ulong)(1+((i+1)%32)*owners/32),(i+1)%32)])).ToArray();
    private static Vector3 Point(double angle)=>new((float)(20*Math.Cos(angle)),0,(float)(20*Math.Sin(angle)));

    [Fact]
    public void ExtractsOnlyCycleAndReusesSuffixForAnotherEntrance()
    {
        var ring=Circle(); var inlet=new RoundaboutCurve(new(1,100),1,false,[new(30,0,0),Point(0)],[ring[0].Key]);
        var builder=new RoundaboutBuilder(ring.Append(inlet));
        var found=builder.Find(1,[inlet.Key], TestContext.Current.CancellationToken)!;
        Assert.Equal(32,found.Ring.Count); Assert.DoesNotContain(inlet.Key,found.Ring);
        Assert.InRange(found.Circularity,.99,1.001);
        Assert.Equal(found.Id,builder.Find(1,[ring[17].Key], TestContext.Current.CancellationToken)!.Id);
    }
    [Fact]
    public void RecognizesInitInsideCycleEvenWhenFirstRepeatedCurveIsElsewhere()
    {
        var ring=Circle(2); var inlet=new RoundaboutCurve(new(1,100),1,false,[new(30,0,0),Point(0)],[ring[20].Key]);
        var found=new RoundaboutBuilder(ring.Append(inlet)).Find(1,[inlet.Key], TestContext.Current.CancellationToken);
        Assert.NotNull(found); Assert.Equal(new ulong[]{1,2},found.PrefabUids);
    }
    [Fact]
    public void StopsTryingEntrancesAfterFirstAcceptedRing()
    {
        var ring=Circle();
        IEnumerable<RoundaboutCurveKey> Entrances()
        { yield return ring[0].Key; throw new InvalidOperationException("Second entrance must not be enumerated."); }
        Assert.NotNull(new RoundaboutBuilder(ring).Find(1,Entrances(), TestContext.Current.CancellationToken));
    }
    [Fact]
    public void MissingBranchDoesNotBecomePermanentFailureAcrossBuildSessions()
    {
        var ring=Circle(); var first=ring[0] with {Next=[ring[1].Key,new(99,0)]};
        Assert.Null(new RoundaboutBuilder(ring.Skip(1).Append(first)).Find(1,[first.Key], TestContext.Current.CancellationToken));
        Assert.NotNull(new RoundaboutBuilder(ring).Find(1,[ring[0].Key], TestContext.Current.CancellationToken));
    }
    [Fact]
    public void RangeLimitFailureDoesNotPoisonShorterEntry()
    {
        var ring=Circle();
        var tail=Enumerable.Range(0,10).Select(i=>new RoundaboutCurve(new((ulong)(20+i),0),(ulong)(20+i),false,
            [new Vector3(i,0,0),new Vector3(i+1,0,0)], [i==9 ? ring[0].Key : new((ulong)(21+i),0)])).ToArray();
        var builder=new RoundaboutBuilder(ring.Concat(tail));
        Assert.Null(builder.Find(1,[tail[0].Key], TestContext.Current.CancellationToken));
        Assert.NotNull(builder.Find(1,[ring[0].Key], TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChoosesInnerBranchFromCurvatureEvenWithEqualInitialTangent(bool left)
    {
        var ring=Circle(left:left);
        var current=ring[0]; var branch=ring[1]; var start=branch.Points[0];var tangent=branch.Points[1]-start;
        var exit=new RoundaboutCurve(new(2,100),2,left,[start,start+tangent,start+tangent*10],[]);
        ring[0]=current with {Next=[exit.Key,branch.Key]};
        Assert.NotNull(new RoundaboutBuilder(ring.Append(exit)).Find(1,[current.Key], TestContext.Current.CancellationToken));
    }
    [Fact]
    public void CombinesExitLanesButKeepsDifferentPortsOnSameExternalPrefab()
    {
        var ring=Circle(); var exits=new[]{
            new RoundaboutCurve(new(2,1),2,false,[Point(0),new(30,0,0)],[],10),
            new RoundaboutCurve(new(2,2),2,false,[Point(0),new(30,0,0)],[],10),
            new RoundaboutCurve(new(2,3),2,false,[Point(0),new(30,0,0)],[],20)};
        // Attach exits to a separate branch reachable from the ring; innermost selector stays on ring.
        var branch=new RoundaboutCurve(new(1,99),1,false,[Point(0),new(30,0,0)],exits.Select(e=>e.Key).ToArray());
        ring[0]=ring[0] with {Next=[ring[1].Key,branch.Key]};
        var found=new RoundaboutBuilder(ring.Append(branch).Concat(exits)).Find(1,[ring[0].Key], TestContext.Current.CancellationToken)!;
        Assert.Equal(2,found.Exits.Count);Assert.Equal(new ulong[]{10,20},found.Exits.Select(e=>e.NodeUid));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyVerifiedDeadEndCountsAsCompleteExit(bool verified)
    {
        var ring = Circle();
        var branch = new RoundaboutCurve(new(1, 99), 1, false,
            [ring[0].Points[^1], new(40, 0, 0)], [new(2, -1)], ExitNode: 10);
        var road = new RoundaboutCurve(new(2, -1), null, false,
            [new(40, 0, 0), new(60, 0, 0)], [], 10, 20) { TerminalExit = verified };
        ring[0] = ring[0] with { Next = [ring[1].Key, branch.Key] };
        var found = new RoundaboutBuilder(ring.Append(branch).Append(road))
            .Find(1, [ring[0].Key], TestContext.Current.CancellationToken);
        if (!verified) { Assert.Null(found); return; }
        Assert.NotNull(found);
        var exit = Assert.Single(found.Exits);
        Assert.Equal(2UL, exit.ExternalItemUid);
        Assert.Equal(20UL, exit.NodeUid);
        var port = Assert.Single(found.ExitPorts);
        Assert.Equal(10UL, port.BoundaryNodeUid);
        Assert.Equal(20UL, port.ExternalNodeUid);
        // Execution is at the departure, not at the distant road's dead end.
        Assert.InRange(port.DepartureToBoundaryMeters, 19, 21);
    }

    [Fact]
    public void RejectsElongatedCycle()
    {
        var ring=Circle().Select(c=>c with {Points=c.Points.Select(p=>new Vector3(p.X*10,p.Y,p.Z)).ToArray()});
        Assert.Null(new RoundaboutBuilder(ring).Find(1,[new(1,0)], TestContext.Current.CancellationToken));
    }
    [Fact]
    public void SectorIsAttemptedOnlyOnceAndCancellationDoesNotMarkItBuilt()
    {
        var index=new RoundaboutMapIndex();var sector=new SectorCoordinate(0,0);
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>index.BuildNewSectors([sector],cts.Token));
        Assert.Equal(0,index.BuiltSectorCount);
        index.BuildNewSectors([sector], TestContext.Current.CancellationToken);index.BuildNewSectors([sector], TestContext.Current.CancellationToken);
        Assert.Equal(1,index.BuiltSectorCount);
    }

    [Fact]
    public void LaterSectorCompletesRingWithoutRebuildingEarlierSector()
    {
        var left=new Node{Uid=10,Position=new(3990,0,0)};
        var right=new Node{Uid=20,Position=new(4010,0,0)};
        PrefabDescriptor Half(Node start,Node end,float startYaw,float endYaw)=>new()
        {
            Nodes=[new ControlNode{Position=start.Position,InputLines=[0],OutputLines=[]},
                   new ControlNode{Position=end.Position,InputLines=[],OutputLines=[0]}],
            NavCurves=[new NavCurve{StartPosition=start.Position,EndPosition=end.Position,
                StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,startYaw),
                EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,endYaw),Length=10*MathF.PI}]
        };
        var index=new RoundaboutMapIndex();
        index.AddPrefab(new Prefab{Uid=1,Nodes=[left,right]},Half(left,right,MathF.PI,0));
        index.BuildNewSectors([new(0,0)],TestContext.Current.CancellationToken);
        Assert.Empty(index.Structures);
        index.AddPrefab(new Prefab{Uid=2,Nodes=[right,left]},Half(right,left,0,MathF.PI));
        index.BuildNewSectors([new(0,0),new(1,0)],TestContext.Current.CancellationToken);
        var result=Assert.Single(index.Structures);
        Assert.Equal(new ulong[]{1,2},result.PrefabUids);
        Assert.Same(result,index.RoadAnnotations[1]);Assert.Same(result,index.RoadAnnotations[2]);
        Assert.Equal(2,index.BuiltSectorCount);
        index.BuildNewSectors([new(0,0),new(1,0)],TestContext.Current.CancellationToken);
        Assert.Single(index.Structures);
    }

    [Fact]
    public void DifferentLaneRingsShareIdentityOnlyWhenPrefabSetsMatch()
    {
        var outer=Circle(2);
        var inner=outer.Select(c=>c with
        {
            Key=c.Key with {CurveIndex=c.Key.CurveIndex+100},
            Next=c.Next.Select(k=>k with {CurveIndex=k.CurveIndex+100}).ToArray(),
            Points=c.Points.Select(p=>p*.8f).ToArray()
        }).ToArray();
        var builder=new RoundaboutBuilder(outer.Concat(inner));
        var a=builder.Find(1,[outer[0].Key],TestContext.Current.CancellationToken)!;
        var b=builder.Find(2,[inner[20].Key],TestContext.Current.CancellationToken)!;
        Assert.Equal(a.Id,b.Id);
        Assert.False(a.Ring.SequenceEqual(b.Ring));
        var single=Circle();
        var other=new RoundaboutBuilder(single).Find(1,[single[0].Key],TestContext.Current.CancellationToken)!;
        Assert.NotEqual(a.Id,other.Id);
    }
}
