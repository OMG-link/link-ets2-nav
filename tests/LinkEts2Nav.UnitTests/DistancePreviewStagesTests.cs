using LinkEts2Nav.Core.Guidance;
namespace LinkEts2Nav.UnitTests;
public sealed class DistancePreviewStagesTests
{
    [Theory]
    [InlineData(1500,2000)] [InlineData(1499.999,1000)]
    [InlineData(750,1000)] [InlineData(749.999,500)]
    [InlineData(350,500)] [InlineData(349.999,200)]
    [InlineData(150,200)] [InlineData(149.999,100)]
    [InlineData(200,200)] [InlineData(120,100)] [InlineData(55,100)]
    [InlineData(2100,null)]
    public void TheFirstPreviewOwnsItsAssignedStagesSemantics(double first,int? expected)
        =>Assert.Equal(expected,DistancePreviewStages.FirstStage(first,[2000,1000,500,200,100]));
    [Theory]
    [InlineData(1500,1000,true)] [InlineData(1499.999,1000,false)]
    [InlineData(750,500,true)] [InlineData(749.999,500,false)]
    [InlineData(350,200,true)] [InlineData(349.999,200,false)]
    [InlineData(150,100,true)] [InlineData(149.999,100,false)]
    [InlineData(2000,1000,true)] [InlineData(500,200,true)]
    [InlineData(2000.001,2000,true)] [InlineData(2000,2000,false)]
    [InlineData(300,100,true)] [InlineData(100,100,false)]
    [InlineData(25,100,false)]
    public void OnlyTheNextStageUsesTheOriginalIntervalMidpoint(double first,int stage,bool expected)
        =>Assert.Equal(expected,DistancePreviewStages.Includes(first,stage,[2000,1000,500,200,100]));

    [Theory]
    [InlineData("camera")] [InlineData("narrow-road")] [InlineData("city")] [InlineData("lane-merge")]
    public void EnvironmentKindsShareTheSameProfile(string kind)
    {
        foreach(double distance in new[]{349.999,350,388,450,500})
        {
            var planner=new EnvironmentPreviewPlanner();var scheduler=new SpeechTriggerScheduler();
            var item=new EnvironmentPreviewEvent("a",kind,distance,distance,d=>d.ToString());
            var plan=planner.Build("e",0,[item],scheduler,true);
            Assert.Equal(distance>=350,plan.Triggers.Any(t=>t.Id=="a:200"));
        }
    }
}
