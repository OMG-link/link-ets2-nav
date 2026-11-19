using System.Numerics;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightReminderTests
{
    [Theory]
    [InlineData(1.00001f,false)]
    [InlineData(1f,true)]
    [InlineData(.00001f,true)]
    [InlineData(0f,false)]
    public void PreparationWindowIsPositiveAndAtMostOneSecond(float remaining,bool expected)
    {
        var policy=new TrafficLightReminderPolicy();
        Step(policy,0,remaining+.4f);Step(policy,.2,remaining+.2f);
        Assert.Equal(expected,Step(policy,.4,remaining).NoticeId is not null);
    }
    [Fact]
    public void ExhaustedRedCountdownWithdrawsAnAlreadyPreparedReminder()
    {
        var policy=new TrafficLightReminderPolicy();
        Step(policy,0,1.4f);Step(policy,.2,1.2f);Assert.NotNull(Step(policy,.4,1).NoticeId);
        Assert.NotEqual("本次红灯等待已提醒",Step(policy,1.4,0).Diagnostic.State);
    }
    private static readonly DateTimeOffset Start=DateTimeOffset.Parse("2026-11-15T00:00:00Z");
    private static readonly TrafficSignalControl Control=new(1,0,0,new(10,0,0),Vector3.UnitX,
        [new(new(10,0,3),Quaternion.Identity)]);
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
    public void CountsDownToTheObservedRedExitAndQueuesOnce()
    {
        var policy=new TrafficLightReminderPolicy();
        Assert.Null(Step(policy,0,2).NoticeId);
        Assert.Null(Step(policy,.2,1.8f).NoticeId);
        Assert.Null(Step(policy,.4,1.6f).NoticeId);
        var notice=Step(policy,1,1);
        Assert.NotNull(notice.NoticeId);Assert.True(notice.Diagnostic.ClockVerified);
        Assert.Null(Step(policy,1.2,.8f).NoticeId);
    }
    [Theory]
    [InlineData(.002)]
    [InlineData(.02)]
    [InlineData(.2)]
    [InlineData(-.002)]
    public void RollingSlowlyIsNotAStoppedVehicle(double speed)
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<5;i++)Assert.Null(Step(policy,i*.2,1f-i*.2f,speed).NoticeId);
    }
    [Fact]
    public void TinyFloatNoiseAtStandstillIsAllowedButTheStopMustPersist()
    {
        var policy=new TrafficLightReminderPolicy();
        Assert.Null(Step(policy,0,1,.0001).NoticeId);
        Assert.Null(Step(policy,.2,.8f,-.0001).NoticeId);
        Assert.NotNull(Step(policy,.4,.6f,.0001).NoticeId);
    }
    [Fact]
    public void HighFrequencySamplesStillAccumulateEnoughTimeToVerifyTheCountdown()
    {
        var policy=new TrafficLightReminderPolicy();
        var notices=new List<TrafficLightDecision>();
        for(int i=0;i<100;i++)notices.Add(Step(policy,i*.01,1.5f-i*.01f));
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
        for(int i=0;i<4;i++)Assert.Null(Step(policy,i*.2,1-i*.2f,phase:phase).NoticeId);
    }
    [Fact]
    public void FrozenSamplesAndWrongCountdownUnitsCannotTrigger()
    {
        var frozen=new TrafficLightReminderPolicy();var accelerated=new TrafficLightReminderPolicy();
        for(int i=0;i<6;i++)
        {
            Assert.Null(Step(frozen,i*.2,.9f).NoticeId);
            Assert.Null(Step(accelerated,i*.2,1f-i*.4f).NoticeId);
        }
        var stale=new TrafficLightReminderPolicy();
        Step(stale,0,1.2f);Step(stale,.2,1);
        var old=Selection(.2,1);
        Assert.Null(stale.Update(Data(2),old,Start.AddSeconds(2),"missing").NoticeId);
    }
    [Fact]
    public void PausingFreezesTheWaitAndDoesNotCreateAnotherReminderOnResume()
    {
        var policy=new TrafficLightReminderPolicy();Step(policy,0,1.4f);Step(policy,.2,1.2f);
        Assert.NotNull(Step(policy,.4,1).NoticeId);
        Assert.Null(policy.Update(Data(10,paused:true),Selection(10,1),Start.AddSeconds(10),"paused").NoticeId);
        Assert.Null(Step(policy,60,1).NoticeId);
        Assert.Null(Step(policy,60.2,.8f).NoticeId);
        Assert.Null(Step(policy,60.4,.6f).NoticeId);
    }
    [Fact]
    public void ANewRedCycleCanRemindAgain()
    {
        var policy=new TrafficLightReminderPolicy();Step(policy,0,1.4f);Step(policy,.2,1.2f);
        var first=Step(policy,.4,1);Assert.NotNull(first.NoticeId);
        Step(policy,1,10,phase:SignalPhase.Green);
        Step(policy,10,1.4f);Step(policy,10.2,1.2f);
        var second=Step(policy,10.4,1);Assert.NotNull(second.NoticeId);Assert.NotEqual(first.NoticeId,second.NoticeId);
    }
    [Fact]
    public void TheShortWindowNeverStartsSynthesisOrQueuesUnpreparedAudio()
    {
        var policy=new TrafficLightReminderPolicy();
        for(int i=0;i<4;i++)Assert.Null(Step(policy,i*.2,1.4f-i*.2f,audioReady:false).NoticeId);
        Assert.NotNull(Step(policy,.8,.6f,audioReady:true).NoticeId);
    }
}
