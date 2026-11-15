using System.Numerics;
using LinkEts2Nav.Core.Guidance;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class NonEnforcingCameraTests
{
    private static string Json()
    {
        using var stream=typeof(NonEnforcingCameraTests).Assembly.GetManifestResourceStream("GermanHwNonEnforcing.json")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    private static CameraCalibrationSet Profile()=>CameraCalibrationSet.Parse(Json());
    private static CameraObservation Camera(string id="55ae453f4c870001:0")=>new(id,
        new(-4286.9375f,7.6210938f,-13208.933f),new(-4286.9375f,7.6210938f,-13208.933f),-Vector3.UnitX,false,
        "/model/speedcamera/speedcamera_hw_de.pmd","speedcam.20")
    {
        LocatorName="0",ModelVariant="default",ActivePartVerified=true,
        MetadataFingerprint="9BB5CD4475CF7701410CF608487BE4EE87D06572101452FE9B37A70C6C4E20CA"
    };
    private static GameDataSnapshot Data(Vector3 position,DateTimeOffset now)=>new(
        new(position.X,position.Y,position.Z,.25,27.5,0,true,false,1,now){CurrentSpeedLimitKph=80},
        RouteSnapshot.Parse([],now),RouteAvailability.None,"session",true);
    private static NearbyMapSnapshot Map(params CameraObservation[] cameras)=>new(new(),cameras,[],[]);

    [Fact]
    public void ProvisionalNonEnforcingProfileNeedsNoInventedDirectionOrOffset()
    {
        var raw=Camera();var result=Profile().Apply(raw);
        Assert.True(result.NonEnforcing);Assert.False(result.DirectionVerified);
        Assert.Contains("provisional user decision",result.NonEnforcingEvidence);
        Assert.Equal(raw.LocatorPosition,result.LocatorPosition);
        Assert.Equal(raw.MonitoredPosition,result.MonitoredPosition);
        Assert.True(result.ActivePartVerified);Assert.False(raw.NonEnforcing);
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("hookup")]
    [InlineData("locator")]
    [InlineData("variant")]
    [InlineData("fingerprint")]
    [InlineData("inactive")]
    public void DifferentResourceBindingsCannotInheritNonEnforcingClassification(string change)
    {
        var camera=Camera();camera=change switch
        {
            "descriptor"=>camera with{SourceDescriptor="/model/other.pmd"},
            "hookup"=>camera with{Hookup="speedcam.r"},
            "locator"=>camera with{LocatorName="1"},
            "variant"=>camera with{ModelVariant="other"},
            "fingerprint"=>camera with{MetadataFingerprint=new('B',64)},
            _=>camera with{ActivePartVerified=false}
        };
        // A previously applied annotation must not survive changed metadata.
        camera=camera with{NonEnforcing=true,NonEnforcingEvidence="stale annotation"};
        var result=Profile().Apply(camera);
        Assert.False(result.NonEnforcing);Assert.Null(result.NonEnforcingEvidence);
        Assert.False(result.DirectionVerified);
    }

    [Fact]
    public void RemovingTheProfileRevokesTheClassification()
    {
        var result=CameraCalibrationSet.Empty.Apply(Profile().Apply(Camera()));
        Assert.False(result.NonEnforcing);Assert.Null(result.NonEnforcingEvidence);
    }

    [Theory]
    [InlineData("\"nonEnforcing\": true", "\"nonEnforcing\": false")]
    [InlineData("\"nonEnforcing\": true", "\"nonEnforcing\": true,\"travelAxis\":\"+Z\"")]
    public void ContradictoryOrIncompleteCalibrationIsRejected(string before,string after)
        =>Assert.Throws<InvalidDataException>(()=>CameraCalibrationSet.Parse(Json().Replace(before,after)));

    [Fact]
    public void BothSavedInstancesStayVisibleInDiagnosticsWithoutWarnings()
    {
        var first=Camera();var second=Camera("55ae45baee070000:0") with
            {LocatorPosition=new(-4797.439f,13.152344f,-13205.663f),MonitoredPosition=new(-4797.439f,13.152344f,-13205.663f)};
        var now=DateTimeOffset.UtcNow;var recorder=new RecordedWarningPipeline();var profile=Profile();
        var snapshot=Map(profile.Apply(first),profile.Apply(second));
        // Saved overspeed positions near the first warning and the second passage.
        recorder.Process(Data(new(-3977.9897f,6.094284f,-13377.012f),now),snapshot,now);
        var diagnostic=recorder.Process(Data(new(-4798.147f,13.172825f,-13201.328f),now.AddSeconds(1)),snapshot,now.AddSeconds(1));
        Assert.Empty(recorder.Queued);Assert.Equal(2,diagnostic.CameraCount);
        Assert.Equal(2,diagnostic.Cameras.Count);
        Assert.All(diagnostic.Cameras,c=>Assert.Contains("暂记不测速",c.Decision));
        Assert.All(diagnostic.Cameras,c=>Assert.Null(c.SpeedLimitKph));
    }

    [Fact]
    public async Task LiveClassificationCancelsOnlyThisCamerasPendingSeries()
    {
        await using var playback=new GuidancePlaybackManager(null,new NoPlayer(),()=>null);
        var guidance=new GuidanceCoordinator(playback:playback);var pipeline=new WarningPipeline(guidance:guidance);
        var camera=Camera() with{LocatorPosition=Vector3.Zero,MonitoredPosition=Vector3.Zero};
        var other=camera with{Id="other",SourceDescriptor="/model/other.pmd",LocatorPosition=new(100,0,0),MonitoredPosition=new(100,0,0)};
        var now=DateTimeOffset.UtcNow;
        void Frame(NearbyMapSnapshot map,int seconds)
        {
            var at=now.AddSeconds(seconds);var data=Data(new(400,0,0),at);
            guidance.Observe(data,at);pipeline.Process(data,map,at);guidance.Tick(data,at);
        }
        Frame(Map(camera,other),0);Assert.Equal(2,playback.Pending.Count);
        Frame(Map(Profile().Apply(camera),other),1);
        Assert.Contains("other",Assert.Single(playback.Pending).EventId);
        var sequence=guidance.Timeline.Sources.Single(s=>s.Source=="landmarks").Sequences
            .Single(s=>s.Events.Any(e=>e.Id.Contains(camera.Id,StringComparison.Ordinal)));
        Assert.True(sequence.Ended);
        Assert.True(guidance.Scheduler.IsConsumed("landmarks",SpeechSequence.EndTriggerId(sequence.Id)));
        Frame(Map(Profile().Apply(camera),other),2);Assert.Single(playback.Pending);
    }
    private sealed class NoPlayer:IGuidanceAudioPlayer
    {
        public Task PlayAsync(string file,CancellationToken cancellationToken)=>throw new InvalidOperationException("No audio is available in this test.");
    }
}
