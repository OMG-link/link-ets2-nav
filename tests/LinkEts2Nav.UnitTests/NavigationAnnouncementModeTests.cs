using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationAnnouncementModeTests
{
    private static readonly DateTimeOffset Now=new(2026,11,9,0,0,0,TimeSpan.Zero);
    private static NavigationManeuver Target(double distance,NavigationAnnouncementMode mode)=>new("merge",ManeuverKind.MergeLeft,distance,"a","b")
        {AnnouncementMode=mode,Instruction="请注意前方车道合并，建议走左侧车道"};
    [Fact]
    public void SilentActionConsumesStagesWithoutAudioOrReadinessCallsThenAllowsContinuation()
    {
        var policy=new NavigationInstructionPolicy();int calls=0;
        int i=0;foreach(double distance in new[]{2200d,2000,1000,500,200,100,20,10})
        {
            var now=Now.AddSeconds(i++);var m=Target(distance,NavigationAnnouncementMode.Silent);
            Assert.Empty(policy.Plan(m,"s",now,now,true));
            Assert.Empty(policy.Update(m,"s",now,now,true,_=>{calls++;return false;}));
        }
        Assert.Equal(0,calls);
        var after=Now.AddSeconds(i);
        Assert.Single(policy.UpdateSequence([],"s",after,after,true,hasConfirmedContinuation:true));
    }
    [Fact]
    public void NoticeHasOnlyOneFiveHundredMetreAnnouncementAndNoExecutionRepeat()
    {
        var policy=new NavigationInstructionPolicy();int i=0;
        foreach(double distance in new[]{700d,501,500,200,20})
        {
            var now=Now.AddSeconds(i++);var output=policy.Update(Target(distance,NavigationAnnouncementMode.NoticeAt500),"s",now,now,true);
            if(distance==500)Assert.Equal("请注意前方车道合并，建议走左侧车道。",Assert.Single(output));else Assert.Empty(output);
        }
    }
    [Fact]
    public void LateNoticeIsImmediateAndUnavailableAudioDoesNotConsumeIt()
    {
        var policy=new NavigationInstructionPolicy();var target=Target(350,NavigationAnnouncementMode.NoticeAt500);
        Assert.Empty(policy.Update(target,"s",Now,Now,true,_=>false));
        Assert.Single(policy.Update(target,"s",Now.AddSeconds(1),Now.AddSeconds(1),true));
    }
}
