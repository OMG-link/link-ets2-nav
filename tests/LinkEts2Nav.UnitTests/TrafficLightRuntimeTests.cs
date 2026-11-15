using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightRuntimeTests
{
    [Fact]
    public async Task ReminderUsesTheSharedQueueAndWithdrawsWhenItsRedWaitExpires()
    {
        string clip=Path.GetTempFileName();
        try
        {
            await using var cache=new SpeechAnnouncementQueue(new NeverSynthesize(),_=>{},preGenerated:
                new Dictionary<string,string>{{TrafficLightReminderRuntime.Text,clip}});
            // An intentionally unavailable playback cache holds the ready notice for expiry inspection.
            await using var player=new GuidancePlaybackManager(null,new NeverPlay(),()=>null);
            var coordinator=new GuidanceCoordinator(cache,player);
            var runtime=new TrafficLightReminderRuntime(coordinator,new());
            var begin=DateTimeOffset.UtcNow;
            var control=new TrafficSignalControl(7,0,0,new(100,0,0),Vector3.UnitX,
                [new(new(100,0,4),Quaternion.Identity)],new(SignalPhase.OrangeToGreen,"PPD"),"known");
            var map=new NearbyMapSnapshot(new(),[],[],[]){TrafficSignals=new([control])};
            var path=new PrefabLanePath(7,1,2,0,0,0,0,1,1,[0],[new(100,0,0),new(130,0,0)],30);
            var navigation=new NavigationDiagnostic("test"){LaneRoute=new([new("prefab:7:0",path)],[],true,null)};
            GameDataSnapshot Sample(double seconds,SignalPhase phase=SignalPhase.Red,double speed=0)
            {
                var now=begin.AddSeconds(seconds);
                return new(new(90,0,0,.75,speed,100,true,false,1,now),RouteSnapshot.Parse([new(1,100,1),new(2,0,0)],now),
                    RouteAvailability.Available,"s",true){TrafficSignals=new(now,[new(1,new(100,0,4),Quaternion.Identity,phase,1.4f-(float)seconds)])};
            }
            coordinator.Observe(Sample(0),begin,processJourney:false);
            runtime.Process(Sample(0),map,navigation,begin);
            runtime.Process(Sample(.2),map,navigation,begin.AddSeconds(.2));
            runtime.Process(Sample(.4),map,navigation,begin.AddSeconds(.4));
            Assert.Equal(TrafficLightReminderRuntime.Text,Assert.Single(player.Pending).Text);
            runtime.Process(Sample(.6,SignalPhase.OrangeToGreen),map,navigation,begin.AddSeconds(.6));
            Assert.Empty(player.Pending);
        }
        finally{File.Delete(clip);}
    }
    private sealed class NeverSynthesize:ISpeechSynthesizer
    {
        public Task<string> SynthesizeAsync(string text,CancellationToken cancellationToken)=>throw new InvalidOperationException("Expected prewarmed audio.");
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class NeverPlay:IGuidanceAudioPlayer
    {public Task PlayAsync(string file,CancellationToken cancellationToken)=>throw new InvalidOperationException("Tests never play audio.");}
}
