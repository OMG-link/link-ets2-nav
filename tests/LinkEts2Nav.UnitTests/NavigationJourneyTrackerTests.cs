using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationJourneyTrackerTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static GameDataSnapshot Data(double seconds,double remaining,ulong[]? nodes=null,
        RouteAvailability availability=RouteAvailability.Available,bool paused=false,string session="s",bool truncated=false)
    {
        var time=Now.AddSeconds(seconds);nodes??=[1,2,3];
        var route=RouteSnapshot.Parse(nodes.Select((uid,i)=>new RouteNode(uid,(nodes.Length-i-1)*1000,10)),time) with{MayBeTruncated=truncated};
        return new(new(0,0,0,0,10,remaining,true,paused,(ulong)(seconds*10+1),time),route,availability,session,!paused);
    }
    [Fact]
    public void StartsOnceAndNormalTrimmingAndExtensionAreSilent()
    {
        var tracker=new NavigationJourneyTracker();
        Assert.Equal(NavigationJourneyNoticeKind.Start,tracker.Update(Data(0,1000,truncated:true),Now).Notice!.Kind);
        Assert.Null(tracker.Update(Data(.2,990,truncated:true),Now.AddSeconds(.2)).Notice);
        Assert.Null(tracker.Update(Data(1,900,[2,3],truncated:true),Now.AddSeconds(1)).Notice);
        Assert.Null(tracker.Update(Data(2,800,[2,3,4]),Now.AddSeconds(2)).Notice);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GoalEditsAndOtherReplacementsUseTheSameReplanningEvent(bool goalChanged)
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        var result=tracker.Update(Data(1,950,goalChanged?[1,2,3,4]:[1,9,3]),Now.AddSeconds(1));
        Assert.Equal(NavigationJourneyNoticeKind.Replanned,result.Notice!.Kind);
        Assert.Null(tracker.Update(Data(2,940,[1,8,5]),Now.AddSeconds(2)).Notice);
        Assert.Equal(NavigationJourneyNoticeKind.Replanned,tracker.Update(Data(7,900,[1,7,5]),Now.AddSeconds(7)).Notice!.Kind);
    }
    [Theory]
    [InlineData(RouteAvailability.Unknown)] [InlineData(RouteAvailability.Stale)] [InlineData(RouteAvailability.Invalid)]
    public void TemporaryUnavailableRouteDoesNotRestartNavigation(RouteAvailability state)
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        Assert.Null(tracker.Update(Data(1,900,availability:state),Now.AddSeconds(1)).Notice);
        Assert.Null(tracker.Update(Data(2,800),Now.AddSeconds(2)).Notice);
    }
    [Fact]
    public void PauseStaleTelemetryAndSessionResetDoNotInventArrival()
    {
        var tracker=new NavigationJourneyTracker();
        Assert.Null(tracker.Update(Data(0,1000,paused:true),Now).Notice);
        Assert.Null(tracker.Update(Data(0,1000),Now.AddSeconds(3)).Notice);
        Assert.Equal(NavigationJourneyNoticeKind.Start,tracker.Update(Data(4,1000),Now.AddSeconds(4)).Notice!.Kind);
        Assert.Null(tracker.Update(Data(5,0,availability:RouteAvailability.None,session:"teleport"),Now.AddSeconds(5)).Notice);
    }
    [Theory]
    [InlineData(100)] [InlineData(5000)]
    public void NavigationClosureClearsTheJourneyWithoutAnyArrivalNotice(double remaining)
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,remaining),Now);
        var cleared=tracker.Update(Data(1,0,availability:RouteAvailability.None),Now.AddSeconds(1));
        Assert.True(cleared.Ended);Assert.True(cleared.WithdrawNotices);Assert.Null(cleared.Notice);
        Assert.Null(tracker.Update(Data(2,0,availability:RouteAvailability.None),Now.AddSeconds(2)).Notice);
        Assert.Equal(NavigationJourneyNoticeKind.Start,tracker.Update(Data(3,1000),Now.AddSeconds(3)).Notice!.Kind);
    }
    [Theory]
    [InlineData(false,2100,61,"准备出发，全程二点一公里，大约需要两分钟。")]
    [InlineData(true,2000,120,"重新规划路线，全程两公里，大约需要两分钟。")]
    [InlineData(false,1200,30,"准备出发，全程一点二公里，大约需要一分钟。")]
    [InlineData(false,20,1,"准备出发，全程二十米，大约需要一分钟。")]
    [InlineData(true,303,12,"重新规划路线，全程三百米，大约需要一分钟。")]
    [InlineData(false,316,20,"准备出发，全程三百二十米，大约需要一分钟。")]
    [InlineData(false,995,40,"准备出发，全程一千米，大约需要一分钟。")]
    [InlineData(false,1000,40,"准备出发，全程一公里，大约需要一分钟。")]
    [InlineData(false,1,1,"准备出发，全程十米，大约需要一分钟。")]
    public void BriefingsUseWorldKilometersAndRoundUpRealMinutes(bool replan,double meters,double seconds,string text)
        =>Assert.Equal(text,NavigationJourneyPrompts.Format(replan?NavigationJourneyNoticeKind.Replanned:NavigationJourneyNoticeKind.Start,meters,seconds));
}
