using System.Numerics;
using LinkEts2Nav.Map;
namespace LinkEts2Nav.Runtime;

public sealed record CurrentSpeedLimitDiagnostic(string SessionId,DateTimeOffset SampledAt,
    double X,double Y,double Z,double HeadingTurns,double? ActualKph,int? InferredKph,string Reason)
{
    public bool Failed => InferredKph is null;
    public bool Mismatch => InferredKph is { } inferred && ActualKph is { } actual && Math.Abs(inferred-actual)>0.5;
}

/// <summary>Compares the same pose's inferred and SDK limits once per second, without caching moving positions.</summary>
public sealed class CurrentSpeedLimitMonitor
{
    private CurrentSpeedLimitDiagnostic? current;
    private object? mapIdentity;
    public CurrentSpeedLimitDiagnostic? Sample(GameDataSnapshot data,object map,SpeedLimitResolver resolver,DateTimeOffset now)
    {
        if(!data.IsVehicleUsable||!data.CanPrepareMap(now)||data.Vehicle is not { Paused:false } vehicle)return null;
        if(current is not null&&current.SessionId==data.SessionId&&ReferenceEquals(mapIdentity,map)
            &&now>=current.SampledAt&&now-current.SampledAt<TimeSpan.FromSeconds(1))return current;
        var result=resolver.ResolvePosition(new((float)vehicle.X,(float)vehicle.Y,(float)vehicle.Z),vehicle.ForwardDirection);
        mapIdentity=map;
        return current=new(data.SessionId,vehicle.ReceivedAt,vehicle.X,vehicle.Y,vehicle.Z,vehicle.HeadingTurns,
            vehicle.CurrentSpeedLimitKph,result.Kph,result.Reason);
    }
    public void Reset(){current=null;mapIdentity=null;}
}
