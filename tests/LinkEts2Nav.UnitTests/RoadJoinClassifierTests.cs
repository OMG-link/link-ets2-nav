using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class RoadJoinClassifierTests
{
    [Fact]
    public void SameRoadTwoLanesBecomingOneIsNotMainRoadEntry()
    {
        LaneConnection[] lanes=[new(1,0,3,0),new(1,1,3,0)];
        Assert.Equal(RoadJoinRole.None,RoadJoinClassifier.Classify(lanes,1,3).Role);
    }
    [Fact]
    public void DistinctRoadContractionAndPreservedMainLanesIdentifyBranch()
    {
        LaneConnection[] lanes=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,2),new(2,1,3,2)];
        Assert.Equal(new(RoadJoinRole.Entering,1),RoadJoinClassifier.Classify(lanes,2,3));
        Assert.Equal(RoadJoinRole.Continuing,RoadJoinClassifier.Classify(lanes,1,3).Role);
    }
    [Fact]
    public void SingleLaneRampJoiningExistingMainLaneHasExplicitSharedDestination()
    {
        LaneConnection[] lanes=[new(1,0,3,0),new(1,1,3,1),new(2,0,3,1)];
        Assert.Equal(RoadJoinRole.Entering,RoadJoinClassifier.Classify(lanes,2,3).Role);
        Assert.Equal(RoadJoinRole.Continuing,RoadJoinClassifier.Classify(lanes,1,3).Role);
    }
    [Fact]
    public void SymmetricLaneAdditionAndUnknownDataDoNotEstablishMainRoad()
    {
        LaneConnection[] lanes=[new(1,0,3,0),new(2,0,3,1)];
        Assert.Equal(RoadJoinRole.Ambiguous,RoadJoinClassifier.Classify(lanes,1,3).Role);
        Assert.Equal(RoadJoinRole.Ambiguous,RoadJoinClassifier.Classify(lanes,2,3).Role);
        Assert.Equal(RoadJoinRole.Ambiguous,RoadJoinClassifier.Classify(null,1,3).Role);
    }
}
