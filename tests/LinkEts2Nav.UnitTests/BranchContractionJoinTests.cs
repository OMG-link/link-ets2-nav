using System.Numerics;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.UnitTests;

public sealed class BranchContractionJoinTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ABranchNarrowingToItsOwnAddedLaneStillNeedsMainEntranceScoring(bool left)
    {
        int side=left?0:3;int first=left?1:0;
        LaneConnection[] lanes=[new(1,0,4,first),new(1,1,4,first+1),new(1,2,4,first+2),new(2,0,4,side),new(2,1,4,side)];
        Assert.Equal(2,RoadJoinTopology.IndependentEntries(lanes,4).Count);
        Assert.Equal(RoadJoinRole.Ambiguous,RoadJoinClassifier.Classify(lanes,2,4).Role);
        var priority=RoadJoinPriorityScorer.Evaluate([new(1,3,0,0,0),new(2,2,4,0,15)]);
        Assert.Equal(1UL,priority.MainEntry);
        Assert.Equal(RoadJoinRole.Continuing,RoadJoinClassifier.Classify(lanes,1,4,priority).Role);
        var selected=new DirectedRoad("prefab:42:2:1",2,4,[new(10,0,100),new(10,0,101),new(0,0,139),new(0,0,140)])
            {LaneConnections=lanes,JoinPriority=priority,NativeEntryTangent=Vector3.UnitZ,NativeExitTangent=Vector3.UnitZ};
        var main=selected with{Id="prefab:42:0:1",StartNodeUid=1};
        var before=new DirectedRoad("before",10,2,[new(10,0,0),new(10,0,100)]);
        var after=new DirectedRoad("after",4,20,[new(0,0,140),new(0,0,500)]);
        var result=new NavigationManeuverDetector().FindSequence([before,selected,after],0,[before,selected,main,after],out _,out bool unknown);
        Assert.False(unknown);Assert.DoesNotContain(result,m=>m.Kind is ManeuverKind.MergeLeft or ManeuverKind.MergeRight);
    }

    [Fact]
    public void AContractionSharingTheMainLanesIsStillAnActualMerge()
    {
        LaneConnection[] lanes=[new(1,0,4,0),new(1,1,4,1),new(1,2,4,2),new(2,0,4,2),new(2,1,4,2)];
        Assert.Empty(RoadJoinTopology.IndependentEntries(lanes,4));
        Assert.Equal(RoadJoinRole.Entering,RoadJoinClassifier.Classify(lanes,2,4).Role);
    }
}
