using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class NavigationJourneyTrackerTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static GameDataSnapshot Data(double seconds,double remaining,ulong[]? nodes=null,
        RouteAvailability availability=RouteAvailability.Available,bool paused=false,string session="s",bool truncated=false)
    {
        var now=Now.AddSeconds(seconds);nodes??=[1,2,3];
        var route=RouteSnapshot.Parse(nodes.Select((uid,i)=>new RouteNode(uid,(nodes.Length-i-1)*1000,10)),now) with{MayBeTruncated=truncated};
        return new(new(0,0,0,0,10,remaining,true,paused,(ulong)(seconds*10+1),now),route,availability,session,!paused);
    }
    private static string Text(NavigationJourneyUpdate result)=>Assert.Single(result.Notifications).Text;
    [Fact]
    public void FixedLifecyclePhrasesAreAvailableToTheSpeechPack()
    {
        var texts=LinkEts2Nav.Speech.SpeechPack.DefaultTexts().ToHashSet();
        foreach(var phrase in new[]{NavigationJourneyTracker.StartText,NavigationJourneyTracker.DestinationChangedText,
            NavigationJourneyTracker.ReplannedText,NavigationJourneyTracker.ArrivedText})Assert.Contains(phrase,texts);
    }
    [Fact]
    public void StartsOnceAndNormalPrefixTrimmingAndTailExtensionStaySilent()
    {
        var tracker=new NavigationJourneyTracker();
        Assert.Equal(NavigationJourneyTracker.StartText,Text(tracker.Update(Data(0,1000,truncated:true),Now)));
        Assert.Empty(tracker.Update(Data(.2,990,truncated:true),Now.AddSeconds(.2)).Notifications);
        Assert.Empty(tracker.Update(Data(1,900,[2,3],truncated:true),Now.AddSeconds(1)).Notifications);
        Assert.Empty(tracker.Update(Data(2,800,[2,3,4]),Now.AddSeconds(2)).Notifications);
    }
    [Fact]
    public void SameGoalReplacementUsesNeutralReplanningAndSuppressesBurstUpdates()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        Assert.Equal(NavigationJourneyTracker.ReplannedText,Text(tracker.Update(Data(1,950,[1,9,3]),Now.AddSeconds(1))));
        Assert.Empty(tracker.Update(Data(2,940,[1,8,3]),Now.AddSeconds(2)).Notifications);
        Assert.Equal(NavigationJourneyTracker.ReplannedText,Text(tracker.Update(Data(7,900,[1,7,3]),Now.AddSeconds(7))));
    }
    [Fact]
    public void ChangingGoalStartsANewJourneyButMissingDestinationDataDoesNotInventAGoal()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        Assert.Equal(NavigationJourneyTracker.DestinationChangedText,Text(tracker.Update(Data(1,950,[1,9,4]),Now.AddSeconds(1))));
        Assert.Equal(NavigationJourneyTracker.ReplannedText,Text(tracker.Update(Data(2,900,[1,8,5],truncated:true),Now.AddSeconds(2))));
    }
    [Fact]
    public void ExtendingACompleteRouteToAnotherGoalIsADestinationChange()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        Assert.Equal(NavigationJourneyTracker.DestinationChangedText,Text(tracker.Update(Data(1,1500,[1,2,3,4]),Now.AddSeconds(1))));
    }
    [Theory]
    [InlineData(RouteAvailability.Unknown)] [InlineData(RouteAvailability.Stale)] [InlineData(RouteAvailability.Invalid)]
    public void TemporaryUnavailableRouteDoesNotEndOrRestartNavigation(RouteAvailability state)
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,1000),Now);
        Assert.Empty(tracker.Update(Data(1,900,availability:state),Now.AddSeconds(1)).Notifications);
        Assert.Empty(tracker.Update(Data(2,800),Now.AddSeconds(2)).Notifications);
    }
    [Fact]
    public void PausedAndStaleTelemetryDoNotStartNavigation()
    {
        var tracker=new NavigationJourneyTracker();
        Assert.Empty(tracker.Update(Data(0,1000,paused:true),Now).Notifications);
        Assert.Empty(tracker.Update(Data(0,1000),Now.AddSeconds(3)).Notifications);
        Assert.Equal(NavigationJourneyTracker.StartText,Text(tracker.Update(Data(4,1000),Now.AddSeconds(4))));
    }
    [Fact]
    public void ArrivalRequiresNearGoalThenSustainedZeroAndEndsOnlyOnce()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,100),Now);
        foreach(double t in new[]{1d,1.5})Assert.Empty(tracker.Update(Data(t,0,availability:RouteAvailability.None),Now.AddSeconds(t)).Notifications);
        var arrived=tracker.Update(Data(2,0,availability:RouteAvailability.None),Now.AddSeconds(2));
        Assert.True(arrived.Ended);Assert.Equal(NavigationJourneyTracker.ArrivedText,Text(arrived));
        Assert.Empty(tracker.Update(Data(3,0,availability:RouteAvailability.None),Now.AddSeconds(3)).Notifications);
        Assert.Equal(NavigationJourneyTracker.StartText,Text(tracker.Update(Data(4,2000),Now.AddSeconds(4))));
    }
    [Fact]
    public void RepeatedSameTelemetryCannotConfirmArrivalAndCancellationFarAwayIsSilent()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,100),Now);
        var zero=Data(1,0,availability:RouteAvailability.None);
        tracker.Update(zero,Now.AddSeconds(1));Assert.Empty(tracker.Update(zero,Now.AddSeconds(2)).Notifications);
        tracker.Reset();tracker.Update(Data(0,5000),Now);
        var cleared=tracker.Update(Data(1,0,availability:RouteAvailability.None),Now.AddSeconds(1));
        Assert.True(cleared.Ended);Assert.Empty(cleared.Notifications);
    }
    [Fact]
    public void SessionResetStartsFreshWithoutClaimingArrival()
    {
        var tracker=new NavigationJourneyTracker();tracker.Update(Data(0,100),Now);
        Assert.Empty(tracker.Update(Data(1,0,availability:RouteAvailability.None,session:"teleport"),Now.AddSeconds(1)).Notifications);
        Assert.Equal(NavigationJourneyTracker.StartText,Text(tracker.Update(Data(2,1000,session:"teleport"),Now.AddSeconds(2))));
    }
}
