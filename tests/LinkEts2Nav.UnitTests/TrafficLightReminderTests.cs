using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightReminderTests
{
    private static readonly DateTimeOffset Start=DateTimeOffset.Parse("2026-11-15T00:00:00Z");
    private static readonly TrafficSignalControl Control=new(1,0,0,new(10,0,0),Vector3.UnitX,
        [new(new(10,0,3),Quaternion.Identity)],new(SignalPhase.OrangeToGreen,"PPD"),"test");
    private static GameDataSnapshot Data(double seconds,double speed=0,bool paused=false)
    {
        var now=Start.AddSeconds(seconds);
        return new(new(0,0,0,.75,speed,0,true,paused,(ulong)(seconds*1000000),now),
            RouteSnapshot.Parse([],now),RouteAvailability.None,"session",!paused);
    }
    private static TrafficSignalSelection Selection(double seconds,float left,SignalPhase phase=SignalPhase.Red)
        =>new(Control,new(5,new(10,0,3),Quaternion.Identity,phase,left),Start.AddSeconds(seconds),10);
    private static TrafficLightDecision Step(TrafficLightReminderPolicy policy,double seconds,float left,double speed=0,
        SignalPhase phase=SignalPhase.Red,bool audioReady=true)
        =>policy.Update(Data(seconds,speed),Selection(seconds,left,phase),Start.AddSeconds(seconds),"missing",audioReady);

    [Fact]
    public void CountsDownToTheKnownRedToAmberTransitionAndQueuesOnce()
    {
        var policy=new TrafficLightReminderPolicy();
        Assert.Null(Step(policy,0,4).NoticeId);
        Assert.Null(Step(policy,.2,3.8f).NoticeId);
        Assert.Null(Step(policy,.4,3.6f).NoticeId);
        var notice=Step(policy,1,3);
        Assert.NotNull(notice.NoticeId);Assert.True(notice.Diagnostic.ClockVerified);
        Assert.Equal(SignalPhase.OrangeToGreen,notice.Diagnostic.NextPhase);
        Assert.Null(Step(policy,1.2,2.8f).NoticeId);
    }
    [Fact]
    public void CountriesWithoutPreparatoryAmberCanUseTheDirectGreenTransition()
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<3;i++)
        {
            double time=i*.2;
            var selection=Selection(time,3.4f-(float)time) with{Control=Control with{Timing=new(SignalPhase.Green,"country")}};
            var decision=policy.Update(Data(time),selection,Start.AddSeconds(time),"missing");
            Assert.Equal(i==2,decision.NoticeId is not null);
            Assert.Equal(SignalPhase.Green,decision.Diagnostic.NextPhase);
        }
    }
    [Theory]
    [InlineData(.002)]
    [InlineData(.02)]
    [InlineData(.2)]
    [InlineData(-.002)]
    public void RollingSlowlyIsNotAStoppedVehicle(double speed)
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<5;i++)Assert.Null(Step(policy,i*.2,3f-i*.2f,speed).NoticeId);
    }
    [Fact]
    public void TinyFloatNoiseAtStandstillIsAllowedButTheStopMustPersist()
    {
        var policy=new TrafficLightReminderPolicy();
        Assert.Null(Step(policy,0,3,.0001).NoticeId);
        Assert.Null(Step(policy,.2,2.8f,-.0001).NoticeId);
        Assert.NotNull(Step(policy,.4,2.6f,.0001).NoticeId);
    }
    [Fact]
    public void HighFrequencySamplesStillAccumulateEnoughTimeToVerifyTheCountdown()
    {
        var policy=new TrafficLightReminderPolicy();
        var notices=new List<TrafficLightDecision>();
        for(int i=0;i<100;i++)notices.Add(Step(policy,i*.01,3.5f-i*.01f));
        Assert.Single(notices,n=>n.NoticeId is not null);
    }
    [Theory]
    [InlineData(SignalPhase.Green)]
    [InlineData(SignalPhase.OrangeToRed)]
    [InlineData(SignalPhase.OrangeToGreen)]
    [InlineData(SignalPhase.Sleep)]
    public void OtherPhasesAreNotMisreportedAsAnUpcomingRedExit(SignalPhase phase)
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<4;i++)Assert.Null(Step(policy,i*.2,3-i*.2f,phase:phase).NoticeId);
    }
    [Fact]
    public void FrozenSamplesAndWrongCountdownUnitsCannotTrigger()
    {
        var frozen=new TrafficLightReminderPolicy();var accelerated=new TrafficLightReminderPolicy();
        for(int i=0;i<6;i++)
        {
            Assert.Null(Step(frozen,i*.2,2.9f).NoticeId);
            Assert.Null(Step(accelerated,i*.2,3f-i*.4f).NoticeId);
        }
        var stale=new TrafficLightReminderPolicy();
        Step(stale,0,3.2f);Step(stale,.2,3);
        var old=Selection(.2,3);
        Assert.Null(stale.Update(Data(2),old,Start.AddSeconds(2),"missing").NoticeId);
    }
    [Fact]
    public void PausingFreezesTheWaitAndDoesNotCreateAnotherReminderOnResume()
    {
        var policy=new TrafficLightReminderPolicy();Step(policy,0,3.4f);Step(policy,.2,3.2f);
        Assert.NotNull(Step(policy,.4,3).NoticeId);
        Assert.Null(policy.Update(Data(10,paused:true),Selection(10,3),Start.AddSeconds(10),"paused").NoticeId);
        Assert.Null(Step(policy,60,3).NoticeId);
        Assert.Null(Step(policy,60.2,2.8f).NoticeId);
        Assert.Null(Step(policy,60.4,2.6f).NoticeId);
    }
    [Fact]
    public void ANewRedCycleCanRemindAgain()
    {
        var policy=new TrafficLightReminderPolicy();Step(policy,0,3.4f);Step(policy,.2,3.2f);
        var first=Step(policy,.4,3);Assert.NotNull(first.NoticeId);
        Step(policy,1,10,phase:SignalPhase.Green);
        Step(policy,10,3.4f);Step(policy,10.2,3.2f);
        var second=Step(policy,10.4,3);Assert.NotNull(second.NoticeId);Assert.NotEqual(first.NoticeId,second.NoticeId);
    }
    [Fact]
    public void TheShortWindowNeverStartsSynthesisOrQueuesUnpreparedAudio()
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<4;i++)Assert.Null(Step(policy,i*.2,3f-i*.2f,audioReady:false).NoticeId);
        Assert.NotNull(Step(policy,.8,2.2f,audioReady:true).NoticeId);
    }
    [Fact]
    public void InvalidAndUnknownNextPhasesRemainSilent()
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<4;i++)
        {
            var s=Selection(i*.2,3f-i*.2f) with{Control=Control with{Timing=null}};
            Assert.Null(policy.Update(Data(i*.2),s,Start.AddSeconds(i*.2),"missing").NoticeId);
        }
    }
}
