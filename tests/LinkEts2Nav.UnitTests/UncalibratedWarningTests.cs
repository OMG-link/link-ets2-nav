using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
namespace LinkEts2Nav.UnitTests;
public sealed class UncalibratedWarningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static GameDataSnapshot Data(double distance, RouteAvailability state = RouteAvailability.None,
        bool usable = true, bool stale = false, double speed = 20) => new(
        new(distance,0,0,0,speed,0,true,!usable,1,stale?Now.AddSeconds(-3):Now),
        RouteSnapshot.Parse([],Now), state, "session", usable);
    private static NearbyMapSnapshot Map(bool active = true, bool verified = false) => new(new(),
        [new("camera",new(0,0,0),new(10000,0,0),new(0,0,0),verified,"/camera.pmd","speedcam.20")
            { ActivePartVerified=active }],[],[]);

    [Theory]
    [InlineData(RouteAvailability.None)]
    [InlineData(RouteAvailability.Available)]
    [InlineData(RouteAvailability.Invalid)]
    [InlineData(RouteAvailability.Stale)]
    public void NoticeUsesLocatorDistanceWithoutDirectionOrRouteAndHasOnlyOneStage(RouteAvailability state)
    {
        var audio = new RecordedWarningPipeline();
        var pipeline = audio;
        pipeline.Process(Data(501,state),Map(),Now);
        Assert.Empty(audio.Queued);
        pipeline.Process(Data(500,state),Map(),Now);
        pipeline.Process(Data(499,state),Map(),Now);
        pipeline.Process(Data(100,state),Map(),Now);
        var warning = Assert.Single(audio.Queued);
        Assert.Contains(":UncalibratedProximity:",warning.EventId);
        Assert.Equal(WarningDistanceKind.Euclidean,Assert.Single(audio.Guidance.Timeline.Sources[0].Events).DistanceKind);
        Assert.Equal(PlannedSpeechAnnouncer.UncalibratedText,warning.Text);
    }

    [Fact]
    public void PauseStaleDataReverseAndInactivePartsCannotProduceNotice()
    {
        var audio = new RecordedWarningPipeline();
        var pipeline = audio;
        pipeline.Process(Data(100,usable:false),Map(),Now);
        pipeline.Process(Data(100,stale:true),Map(),Now);
        pipeline.Process(Data(100,speed:-1),Map(),Now);
        pipeline.Process(Data(100),Map(active:false),Now);
        pipeline.Process(Data(100),Map(verified:true),Now);
        Assert.Empty(audio.Queued);
    }

    [Fact]
    public void MissingScanDoesNotResetNoticeAndNewApproachAfterPassCanRearm()
    {
        var audio = new RecordedWarningPipeline();
        var pipeline = audio;
        pipeline.Process(Data(499),Map(),Now);
        pipeline.Process(Data(490),new(new(),[],[],[]),Now);
        pipeline.Process(Data(480),Map(),Now);
        Assert.Single(audio.Queued);
        foreach(double d in new[]{10d,50,60,70})pipeline.Process(Data(d),Map(),Now);
        Assert.Single(audio.Queued);
        pipeline.Process(Data(60),Map(),Now);
        Assert.Equal(2,audio.Queued.Count);
    }

    [Fact]
    public void PackAndRuntimeUseExactRequestedMessageEvenForLateDiscovery()
    {
        const string expected="正靠近未校准的探头，无法获取其监测方向，请减速慢行。";
        Assert.Equal(expected,PlannedSpeechAnnouncer.Text(500,150,true,50,CameraWarningKind.UncalibratedProximity));
        Assert.Contains(expected,LinkEts2Nav.Speech.SpeechPack.DefaultTexts());
    }
}
