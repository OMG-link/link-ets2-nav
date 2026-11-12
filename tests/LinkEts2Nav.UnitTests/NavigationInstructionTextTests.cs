using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationInstructionTextTests
{
    [Fact]
    public void CustomActionTextSurvivesPlanningReadinessAndEveryStage()
    {
        var now=DateTimeOffset.UtcNow;
        var policy=new NavigationInstructionPolicy();
        var action=new NavigationManeuver("custom",ManeuverKind.TurnRight,200,"in","out")
            {Instruction="测试动作",EndDistanceMeters=230};
        Assert.Contains("两百米后，测试动作。",policy.Plan(action,"session",now,now,true));
        Assert.Equal("两百米后，测试动作。",Assert.Single(policy.Update(action,"session",now,now,true,t=>t.Contains("测试动作"))));
        action=action with{DistanceMeters=100,EndDistanceMeters=130};now=now.AddSeconds(1);
        Assert.Equal("一百米后，测试动作。",Assert.Single(policy.Update(action,"session",now,now,true)));
        action=action with{DistanceMeters=20,EndDistanceMeters=50};now=now.AddSeconds(1);
        Assert.Equal("测试动作。",Assert.Single(policy.Update(action,"session",now,now,true)));
    }
}
