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

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,false)]
    [InlineData(true,true)]
    [InlineData(false,true)]
    public void ParallelEntranceLanesUseTheRoadsInnerSideOrTheActualPrefabSeam(bool road,bool left)
    {
        var ring=Circle(2,left);
        var current=ring[15];var next=ring[16];var end=current.Points[^1];
        var heading=Vector3.Normalize(end-current.Points[^2]);
        var outward=new Vector3(-heading.Z,0,heading.X)*(left?-1:1);
        var start=end+4.5f*outward;
        // The exit-only lane bends slightly further inward, but starts one lane away.
        var exit=new RoundaboutCurve(new(2,100),2,left,[start,start+heading*4-outward*.3f],[],EntryNode:50){EntryLane=7};
        ring[16]=next with{EntryNode=50,EntryLane=1};
        ring[15]=current with{PrefabUid=road?null:current.PrefabUid,ExitNode=50,ExitLane=road?-1:7,
            RoadNormals=road?Enumerable.Repeat(outward,current.Points.Count).ToArray():null,Next=[exit.Key,next.Key]};
        var found=new RoundaboutBuilder(ring.Append(exit)).Find(1,[ring[0].Key],TestContext.Current.CancellationToken);
        Assert.NotNull(found);Assert.Contains(next.Key,found.Ring);Assert.DoesNotContain(exit.Key,found.Ring);
    }

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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinesExitLanesByBoundaryRegardlessOfSharedExternalDestination(bool externalLoaded)
    {
        var ring = Circle();
        RoundaboutCurve Exit(int index, ulong node) => new(new(1, index), 1, false,
            [ring[0].Points[^1], new(40, 0, 0)], [new(900, index)], ExitNode: node);
        var lanes = new[] { Exit(100, 10), Exit(101, 10), Exit(102, 20) };
        ring[0] = ring[0] with { Next = [ring[1].Key, .. lanes.Select(c => c.Key)] };
        var external = lanes.Select(c => new RoundaboutCurve(c.Next[0], 900, false,
            [new(40, 0, 0), new(60, 0, 0)], [], EntryNode: 500));
        var graph = ring.Concat(lanes).Concat(externalLoaded ? external : []);
        var found = new RoundaboutBuilder(graph).Find(1, [ring[0].Key], TestContext.Current.CancellationToken)!;
        Assert.Equal(new ulong[] { 10, 20 }, found.Exits.Select(e => e.NodeUid));
        Assert.Equal(2, found.Exits[0].Branches.Count);
        Assert.Equal(new ulong[] { 10, 20 }, found.ExitPorts.Select(p => p.BoundaryNodeUid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalRoadCompletenessDoesNotChangeBoundaryExit(bool loaded)
    {
        var ring = Circle();
        var branch = new RoundaboutCurve(new(1, 99), 1, false,
            [ring[0].Points[^1], new(40, 0, 0)], [new(2, -1)], ExitNode: 10);
        var road = new RoundaboutCurve(new(2, -1), null, false,
            [new(40, 0, 0), new(60, 0, 0)], [], 10, 20);
        ring[0] = ring[0] with { Next = [ring[1].Key, branch.Key] };
        var graph = ring.Append(branch).Concat(loaded ? [road] : []);
        var found = new RoundaboutBuilder(graph).Find(1, [ring[0].Key], TestContext.Current.CancellationToken)!;
        Assert.Equal(10UL, Assert.Single(found.Exits).NodeUid);
        var port = Assert.Single(found.ExitPorts);
        Assert.Equal(10UL, port.BoundaryNodeUid);
        // Execution stays at the departure, not the outer boundary or road end.
        Assert.Contains(branch.Points[0],port.DepartureCandidates);
    }

    [Fact]
    public void CompositeRingExcludesInternalPrefabAndRoadInterfaces()
    {
        var ring = Circle(2);
        // Interface 50 joins two member prefabs directly. Interface 60/70 uses
        // an ordinary road belonging to the ring, in both travel directions.
        ring[15] = ring[15] with { ExitNode = 50 };
        ring[16] = ring[16] with { EntryNode = 50 };
        ring[31] = ring[31] with { ExitNode = 60, Next = [new(3, -1)],
            Points = [.. ring[31].Points.Take(4), ring[0].Points[0] + new Vector3(0, 0, 1)] };
        ring[0] = ring[0] with { EntryNode = 70 };
        var road = new RoundaboutCurve(new(3, -1), null, false,
            [ring[31].Points[^1], ring[0].Points[0]], [ring[0].Key], 60, 70);
        var reverseRoad = new RoundaboutCurve(new(3, -2), null, false,
            road.Points.Reverse().ToArray(), [], 70, 60);
        var exit = new RoundaboutCurve(new(2, 99), 2, false,
            [ring[20].Points[^1], new(-40, 0, 0)], [], ExitNode: 80);
        ring[20] = ring[20] with { Next = [ring[21].Key, exit.Key] };
        var found = new RoundaboutBuilder(ring.Append(road).Append(reverseRoad).Append(exit))
            .Find(1, [ring[0].Key], TestContext.Current.CancellationToken)!;
        Assert.Equal(80UL, Assert.Single(found.ExitPorts).BoundaryNodeUid);
        Assert.DoesNotContain(found.EntryPorts, p => p.NodeUid is 50 or 60 or 70);
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
    public void BoundaryGapIsReportedButAnInvalidInternalCurveIsNotASectorDependency()
    {
        var boundary = new RoundaboutCurve(new(1, 0), 1, false,
            [new(0, 0, 0), new(10, 0, 0)], [], ExitNode: 99);
        var incomplete = new RoundaboutBuilder([boundary]);
        Assert.Null(incomplete.Find(1, [boundary.Key], TestContext.Current.CancellationToken));
        Assert.Contains(99UL, incomplete.UnresolvedBoundaryNodes);
        var malformed = new RoundaboutBuilder([boundary with { Next = [new(1, 42)] }]);
        Assert.Null(malformed.Find(1, [boundary.Key], TestContext.Current.CancellationToken));
        Assert.Empty(malformed.UnresolvedBoundaryNodes);
    }

    [Fact]
    public void SharedMissingBoundaryRetainsEveryActualSearchEntrance()
    {
        var first=new RoundaboutCurve(new(1,0),1,false,[new(-900,0,0),new(-800,0,0)],[]);
        var near=new RoundaboutCurve(new(1,1),1,false,[new(10,0,0),new(100,0,0)],[new(1,3)]);
        var far=new RoundaboutCurve(new(1,2),1,false,[new(3000,0,0),new(100,0,0)],[new(1,3)]);
        var tail=new RoundaboutCurve(new(1,3),1,false,[new(100,0,0),new(200,0,0)],[],ExitNode:99);
        var builder=new RoundaboutBuilder([first,near,far,tail]);
        Assert.Null(builder.Find(1,[far.Key,near.Key],TestContext.Current.CancellationToken));
        Assert.Equal(2,builder.BoundaryDependencies.Count);
        Assert.All(builder.BoundaryDependencies,d=>Assert.Equal(99UL,d.NodeUid));
        Assert.Equal(new[]{near.Points[0],far.Points[0]}.OrderBy(p=>p.X),
            builder.BoundaryDependencies.Select(d=>d.QueryAnchor).OrderBy(p=>p.X));
        Assert.DoesNotContain(builder.BoundaryDependencies,d=>d.QueryAnchor==first.Points[0]);
    }

    [Fact]
    public void TwoPrefabsInOneSearchCellKeepBothSeedsForTheSameMissingAttachment()
    {
        var end=new Node{Uid=99,Position=new(3900,0,0)};
        var index=new RoundaboutMapIndex();
        void Add(ulong uid,float x)
        {
            var start=new Node{Uid=uid+100,Position=new(x,0,0)};
            var descriptor=new PrefabDescriptor
            {
                Nodes=[new ControlNode{Position=start.Position,InputLines=[0],OutputLines=[]},
                    new ControlNode{Position=end.Position,InputLines=[],OutputLines=[0]}],
                NavCurves=[new NavCurve{StartPosition=start.Position,EndPosition=end.Position,
                    StartRotation=Quaternion.Identity,EndRotation=Quaternion.Identity,Length=3900-x}]
            };
            index.AddPrefab(new Prefab{Uid=uid,Nodes=[start,end]},descriptor);
        }
        Add(1,3000);Add(2,10);
        index.BuildNewSectors([new(0,0)],TestContext.Current.CancellationToken,
            _=>new MissingMapObjects([new MapObjectKey(MapObjectKind.Item,10)],"shared missing owner"));
        Assert.Equal(2,index.MapIssues.Count);
        Assert.Equal(new float[]{10,3000},index.MapIssues.Select(i=>i.QueryAnchor!.Value.X).Order());
    }

    [Fact]
    public void NewlyAvailableAttachmentRetriesAnAlreadyAttemptedSector()
    {
        var left = new Node { Uid = 10, Position = new(3990, 0, 0) };
        var right = new Node { Uid = 20, Position = new(4010, 0, 0) };
        PrefabDescriptor Half(Node start, Node end, float a, float b) => new()
        {
            Nodes = [new ControlNode { Position = start.Position, InputLines = [0], OutputLines = [] },
                new ControlNode { Position = end.Position, InputLines = [], OutputLines = [0] }],
            NavCurves = [new NavCurve { StartPosition = start.Position, EndPosition = end.Position,
                StartRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, a),
                EndRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b), Length = 10 * MathF.PI }]
        };
        var index = new RoundaboutMapIndex();
        var sectors = new[] { new SectorCoordinate(0, 0), new SectorCoordinate(1, 0) };
        index.AddPrefab(new Prefab { Uid = 1, Nodes = [left, right] }, Half(left, right, MathF.PI, 0));
        index.BuildNewSectors(sectors, TestContext.Current.CancellationToken,
            _=>new MissingMapObjects([new MapObjectKey(MapObjectKind.Item,10)],"missing next owner"){QueryAnchor=new(50000,0,0)});
        Assert.Empty(index.Structures);
        Assert.Equal(2, index.BuiltSectorCount);
        Assert.Contains(20UL, index.UnresolvedBoundaryNodes[sectors[0]]);
        Assert.Equal(left.Position,Assert.Single(index.MapIssues).QueryAnchor);
        index.AddPrefab(new Prefab { Uid = 2, Nodes = [right, left] }, Half(right, left, 0, MathF.PI));
        Assert.Equal(1, index.BuiltSectorCount);
        // The old search cell need not still belong to the current active window.
        index.BuildNewSectors([sectors[1]], TestContext.Current.CancellationToken);
        Assert.Single(index.Structures);
        Assert.Empty(index.UnresolvedBoundaryNodes);
        Assert.Equal(2, index.BuiltSectorCount);
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
    public void ChangedPlayerProjectionInvalidatesOnlyAffectedRingAndBuildVersion()
    {
        var left=new Node{Uid=10,Position=new(3990,0,0)};
        var right=new Node{Uid=20,Position=new(4010,0,0)};
        PrefabDescriptor Half(Node start,Node end,float a,float b)=>new()
        {
            Nodes=[new ControlNode{Position=start.Position,InputLines=[0],OutputLines=[]},new ControlNode{Position=end.Position,InputLines=[],OutputLines=[0]}],
            NavCurves=[new NavCurve{StartPosition=start.Position,EndPosition=end.Position,StartRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,a),EndRotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,b),Length=10*MathF.PI}]
        };
        var first=new Prefab{Uid=1,Nodes=[left,right]};var second=new Prefab{Uid=2,Nodes=[right,left]};
        var index=new RoundaboutMapIndex();var sectors=new[]{new SectorCoordinate(0,0),new SectorCoordinate(1,0)};
        var firstHalf=Half(left,right,MathF.PI,0);
        var open=PrefabPlayerAccess.Project(firstHalf,[true]);
        var closed=PrefabPlayerAccess.Project(firstHalf,[false]);
        index.AddPrefab(first,open);
        index.AddPrefab(second,Half(right,left,0,MathF.PI));
        index.BuildNewSectors(sectors,TestContext.Current.CancellationToken);
        Assert.Single(index.Structures);Assert.Equal(2,index.BuiltSectorCount);
        index.AddPrefab(first,closed);
        Assert.Empty(index.Structures);Assert.Equal(0,index.BuiltSectorCount);
        index.BuildNewSectors(sectors,TestContext.Current.CancellationToken);
        Assert.Empty(index.Structures);Assert.Equal(2,index.BuiltSectorCount);
        index.AddPrefab(first,closed);
        Assert.Equal(2,index.BuiltSectorCount);
        index.AddPrefab(first,open);
        index.BuildNewSectors(sectors,TestContext.Current.CancellationToken);
        Assert.Single(index.Structures);Assert.Equal(2,index.BuiltSectorCount);
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
