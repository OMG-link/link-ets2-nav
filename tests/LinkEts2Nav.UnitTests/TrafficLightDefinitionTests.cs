using System.Numerics;
using LinkEts2Nav.Core;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;
using NativeSignal=ETS2LA.Game.SDK.Semaphore;
using NativeType=ETS2LA.Game.SDK.SemaphoreType;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightDefinitionTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-19T00:00:00Z");
    private static (TrafficSignalControl Control,NearbyMapSnapshot Map,NavigationDiagnostic Navigation) Fixture(SemaphoreType type,bool custom)
    {
        var descriptor=new PrefabDescriptor{Nodes=[new(){Position=Vector3.Zero},new(){Position=new(0,0,30)}],
            NavCurves=[new(){Length=10,SemaphoreId=4,StartRotation=Quaternion.Identity}],
            Semaphores=[new(){Type=type,SemaphoreId=4,Position=new(0,0,3),Rotation=Quaternion.Identity},
                new(){Type=SemaphoreType.ModelOnly,SemaphoreId=4,Position=new(3,0,3),Rotation=Quaternion.Identity}]};
        var prefab=new Prefab{Uid=7,CustomSemaphores=custom,SemaphoreProfile="cr_1x1",
            Nodes=[new Node{Position=Vector3.Zero},new Node{Position=new(0,0,30)}]};
        var control=Assert.Single(TrafficSignalMap.Build(prefab,descriptor));
        var map=new NearbyMapSnapshot(new(),[],[],[]){TrafficSignals=new([control])};
        var path=new PrefabLanePath(7,1,2,0,0,0,0,1,1,[0],[control.StopPoint,control.StopPoint+control.Forward*30],30);
        var navigation=new NavigationDiagnostic("test"){LaneRoute=new([new("prefab:7:0",path,0)],[],true,null)};
        return(control,map,navigation);
    }
    private static NativeSignal Live(TrafficSignalLamp lamp,NativeType type,int id=1,float left=1,SignalPhase phase=SignalPhase.Red)
        =>new(){type=type,id=id,position=lamp.Position,
            rotation=new(lamp.Rotation.W,lamp.Rotation.X,lamp.Rotation.Y,lamp.Rotation.Z),state=(int)phase,time_remaining=left};
    private static GameDataSnapshot Data(TrafficSignalControl control,params NativeSignal[] signals)
    {
        var p=control.StopPoint-control.Forward*10;
        var heading=Math.Atan2(-control.Forward.X,-control.Forward.Z)/Math.Tau;
        return new(new(p.X,p.Y,p.Z,heading,0,100,true,false,1,Now),
            RouteSnapshot.Parse([new(1,100,1),new(2,0,0)],Now),RouteAvailability.Available,"s",true)
            {TrafficSignals=TrafficSignalAdapter.Copy(signals,Now)};
    }
    [Theory]
    [InlineData(SemaphoreType.UseProfile,false)]
    [InlineData(SemaphoreType.UseProfile,true)]
    [InlineData(SemaphoreType.ModelOnly,false)]
    [InlineData(SemaphoreType.TrafficLight,true)]
    public void StaticProfilesAndVisualHeadsRequireActualSdkTrafficLightBinding(SemaphoreType type,bool custom)
    {
        var f=Fixture(type,custom);var binder=new TrafficSignalBinder();
        Assert.Equal(2,f.Control.Lamps.Count);
        Assert.Null(binder.Match(Data(f.Control),f.Map,f.Navigation,out _));
        var lamp=f.Control.Lamps[0];
        Assert.NotNull(binder.Match(Data(f.Control,Live(lamp,NativeType.TRAFFICLIGHT)),f.Map,f.Navigation,out _));
        Assert.Null(binder.Match(Data(f.Control,Live(lamp,NativeType.GATE)),f.Map,f.Navigation,out _));
        // A gate at the same pose must not contaminate the matched traffic-light group's phase.
        Assert.NotNull(binder.Match(Data(f.Control,Live(lamp,NativeType.GATE,2,20,SignalPhase.Green),
            Live(lamp,NativeType.TRAFFICLIGHT)),f.Map,f.Navigation,out _));
    }
    [Fact]
    public void MultipleRealHeadsInAProfileGroupMustAgreeOnPhaseAndCountdown()
    {
        var f=Fixture(SemaphoreType.UseProfile,false);var a=f.Control.Lamps[0];var b=f.Control.Lamps[1];var binder=new TrafficSignalBinder();
        Assert.NotNull(binder.Match(Data(f.Control,Live(a,NativeType.TRAFFICLIGHT),Live(b,NativeType.TRAFFICLIGHT,2)),f.Map,f.Navigation,out _));
        Assert.Null(binder.Match(Data(f.Control,Live(a,NativeType.TRAFFICLIGHT),Live(b,NativeType.TRAFFICLIGHT,2,1,SignalPhase.Green)),f.Map,f.Navigation,out _));
        Assert.Null(binder.Match(Data(f.Control,Live(a,NativeType.TRAFFICLIGHT),Live(b,NativeType.TRAFFICLIGHT,2,1.5f)),f.Map,f.Navigation,out _));
    }
}
