using LinkEts2Nav.Core;
namespace LinkEts2Nav.UnitTests;

public sealed class NavigationInstructionPolicyTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-13T00:00:00Z");
    private static NavigationManeuver M(double distance,string id="action",ManeuverKind kind=ManeuverKind.TurnLeft)
        =>new(id,kind,distance,"entry","exit"){JunctionId=id,EndDistanceMeters=distance+50};
    private static IReadOnlyList<string> Plan(NavigationInstructionPolicy p,params NavigationManeuver[] input)
        =>p.PlanSequence(input,"s",Now,Now,true);
    private static IReadOnlyList<string> Update(NavigationInstructionPolicy p,NavigationManeuver[] input,Func<string,bool>? ready=null)
        =>p.UpdateSequence(input,"s",Now,Now,true,ready);

    [Theory]
    [InlineData(59.9,false)] [InlineData(60,true)] [InlineData(120,true)]
    public void HundredMeterStageUsesTriggerTimeSpeedAndKeepsExecution(double speed,bool skipped)
    {
        var p=new NavigationInstructionPolicy();Update(p,[M(200)]);
        Assert.Contains("一百米后，左转。",Plan(p,M(150)));
        var result=p.UpdateSequence([M(100)],"s",Now,Now,true,speedMetersPerSecond:speed/3.6);
        if(skipped)Assert.Empty(result);else Assert.Equal("一百米后，左转。",Assert.Single(result));
        Assert.Empty(Update(p,[M(90)]));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
    }
    [Fact]
    public void FastHundredPointIsConsumedEvenWhenItsAudioIsUnavailable()
    {
        var p=new NavigationInstructionPolicy();Assert.Contains("一百米后，左转。",Plan(p,M(100)));
        Assert.Empty(p.UpdateSequence([M(100)],"s",Now,Now,true,_=>false,speedMetersPerSecond:60/3.6));
        Assert.Empty(Update(p,[M(90)]));Assert.Equal("左转。",Assert.Single(Update(p,[M(20)])));
    }
    [Fact]
    public void PreparationDoesNotConsumePointsOrInventUnplannedDistanceVariants()
    {
        var p=new NavigationInstructionPolicy();var planned=Plan(p,M(275),M(800,"next",ManeuverKind.TurnRight));
        Assert.Contains("两百五十米后，左转。",planned);
        Assert.Contains("一百米后，左转。",planned);
        Assert.Contains("四百五十米后，右转。",planned);
        Assert.DoesNotContain("两百米后，左转。",planned);
        Assert.Equal("两百五十米后，左转。",Assert.Single(Update(p,[M(275)])));
        Assert.Empty(Update(p,[M(200)]));
    }
    [Fact]
    public void UnreadySpeechKeepsItsFixedTextAndLaterStagesCoverIt()
    {
        var p=new NavigationInstructionPolicy();Assert.Empty(Update(p,[M(700)],_=>false));
        Assert.Equal("七百米后，左转。",Assert.Single(Update(p,[M(675)])));
        Assert.Empty(Update(p,[M(500)]));
        Assert.Equal("两百米后，左转。",Assert.Single(Update(p,[M(200)])));
        var q=new NavigationInstructionPolicy();Update(q,[M(2500)]);
        Assert.Empty(Update(q,[M(2000)],_=>false));
        Assert.Equal("两公里后，左转。",Assert.Single(Update(q,[M(1750)])));
        Assert.Equal("一公里后，左转。",Assert.Single(Update(q,[M(1000)])));
    }
    [Fact]
    public void ReadyExecutionPreventsAnOlderUnreadyPreviewFromResurrecting()
    {
        var p=new NavigationInstructionPolicy();Update(p,[M(500)],_=>false);
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20)],text=>text=="左转。")));
        Assert.Empty(Update(p,[M(19)]));Assert.Empty(Plan(p,M(18)));
    }
    [Fact]
    public void NextActionUsesTheRecordedPhysicalEndRatherThanDisappearingEntry()
    {
        var p=new NavigationInstructionPolicy();Plan(p,M(100),M(600,"next",ManeuverKind.TurnRight));
        Assert.Equal("左转。",Assert.Single(Update(p,[M(20),M(520,"next",ManeuverKind.TurnRight)])));
        Assert.Empty(Update(p,[M(450,"next",ManeuverKind.TurnRight)]));
        Assert.Equal("四百五十米后，右转。",Assert.Single(Update(p,[M(449,"next",ManeuverKind.TurnRight)])));
    }
    [Fact]
    public void AnUnknownPreviousEndDoesNotReleaseTheNextBriefing()
    {
        var p=new NavigationInstructionPolicy();Plan(p,M(100) with{EndDistanceMeters=null},M(600,"next"));
        Assert.Empty(Update(p,[M(20) with{EndDistanceMeters=null},M(520,"next")]));
        Assert.Empty(Update(p,[M(500,"next")]));
        Assert.DoesNotContain("五百米后，左转。",Plan(p,M(500,"next")));
    }
    [Theory]
    [InlineData(false,0)] [InlineData(true,3)] [InlineData(true,-1)]
    public void InvalidSamplesCannotRegisterOrConsume(bool active,int age)
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(p.PlanSequence([M(100)],"s",Now.AddSeconds(-age),Now,active));
        Assert.Empty(p.UpdateSequence([M(100)],"s",Now.AddSeconds(-age),Now,active));
        Assert.Equal("五百米后，左转。",Assert.Single(Update(p,[M(500)])));
    }
    [Fact]
    public void DuplicateUnsortedAndNonfiniteInputsAreRejected()
    {
        var p=new NavigationInstructionPolicy();
        Assert.Empty(Update(p,[M(500),M(100,"next")]));
        Assert.Empty(Update(p,[M(double.NaN)]));Assert.Empty(Update(p,[M(10),M(20)]));
    }
    [Fact]
    public void PausingAndTemporaryMissingInputsDoNotEraseTheSharedLedger()
    {
        var p=new NavigationInstructionPolicy();Update(p,[M(500)]);
        Assert.Empty(p.UpdateSequence([M(490)],"s",Now,Now,false));
        Assert.Empty(Update(p,[]));Assert.Empty(Update(p,[M(490)]));
        Assert.Equal("两百米后，左转。",Assert.Single(Update(p,[M(200)])));
    }
    [Fact]
    public void EndedSequencesAndOlderObservationsCannotReplay()
    {
        var p=new NavigationInstructionPolicy();Update(p,[M(10)]);
        Assert.Empty(p.UpdateSequence([M(700)],"s",Now.AddSeconds(-1),Now,true));
        Assert.Empty(Update(p,[M(0)]));Assert.Empty(Update(p,[]));
        Assert.Single(p.UpdateSequence([M(500)],"new-session",Now,Now,true));
    }
    [Fact]
    public void ConfirmedEmptyCorridorSpeaksOnceAndExplicitResetStartsANewSession()
    {
        var p=new NavigationInstructionPolicy();Assert.Empty(Update(p,[]));
        Assert.Equal(NavigationPrompts.Continue(),Assert.Single(p.UpdateSequence([],"s",Now,Now,true,hasConfirmedContinuation:true,confirmedContinuationDistanceMeters:1200)));
        Assert.Empty(p.UpdateSequence([],"s",Now,Now,true,hasConfirmedContinuation:true,confirmedContinuationDistanceMeters:1200));
        p.Reset();Assert.Equal(NavigationPrompts.Continue(),Assert.Single(p.UpdateSequence([],"s",Now,Now,true,hasConfirmedContinuation:true,confirmedContinuationDistanceMeters:1200)));
    }
    [Theory]
    [InlineData(999,"沿当前道路，继续行驶九百五十米。")]
    [InlineData(1250,"沿当前道路，继续行驶一点二公里。")]
    [InlineData(2650,"沿当前道路，继续行驶二点六公里。")]
    [InlineData(9876,"沿当前道路，继续行驶九点八公里。")]
    [InlineData(10000,"沿当前道路，继续行驶十公里。")]
    [InlineData(10849,"沿当前道路，继续行驶十公里。")]
    [InlineData(12345,"沿当前道路，继续行驶十二公里。")]
    public void LongDistancesUseCoarserRounding(double distance,string expected)=>Assert.Equal(expected,NavigationPrompts.Continue(distance));
    [Theory]
    [InlineData(50,"五十米后，左转。")][InlineData(950,"九百五十米后，左转。")]
    [InlineData(1000,"一公里后，左转。")][InlineData(1500,"一点五公里后，左转。")]
    [InlineData(1750,"一点七五公里后，左转。")][InlineData(2000,"两公里后，左转。")]
    public void DistanceWordingUsesAfterAndSwitchesToKilometers(int meters,string expected)
    {
        Assert.Equal(expected,NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,meters));
        Assert.Equal(expected,LinkEts2Nav.Speech.SpeechPack.NavigationDistance(meters)+"后，左转。");
    }
    [Theory]
    [InlineData(200,"两百米后，左转。")][InlineData(250,"两百五十米后，左转。")][InlineData(2000,"两公里后，左转。")]
    public void IntegersUseLiang(int meters,string expected)=>Assert.Equal(expected,NavigationPrompts.AtDistance(ManeuverKind.TurnLeft,meters));
    [Theory]
    [InlineData(1200,"一点二公里")][InlineData(2100,"二点一公里")][InlineData(2200,"二点二公里")]
    public void FractionsRetainEr(int meters,string expected)=>Assert.Equal("沿当前道路，继续行驶"+expected+"。",NavigationPrompts.Continue(meters));
}
