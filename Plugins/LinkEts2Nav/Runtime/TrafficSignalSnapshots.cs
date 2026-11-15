using System.Numerics;
using NativeSignal=ETS2LA.Game.SDK.Semaphore;

namespace LinkEts2Nav.Runtime;

public enum SignalPhase { Off=0,OrangeToRed=1,Red=2,OrangeToGreen=4,Green=8,Sleep=32 }
public sealed record TrafficSignalObservation(int Id,Vector3 Position,Quaternion Rotation,SignalPhase Phase,float PhaseRemaining);
public sealed record TrafficSignalSnapshot(DateTimeOffset ReceivedAt,IReadOnlyList<TrafficSignalObservation> Signals)
{
    public static TrafficSignalSnapshot Empty {get;}=new(default,[]);
}

/// <summary>Adapts the fixed host's wire-order quaternion to Numerics without opening another SDK reader.</summary>
public static class TrafficSignalAdapter
{
    public static TrafficSignalSnapshot Copy(IEnumerable<NativeSignal> signals,DateTimeOffset receivedAt)
    {
        var copy=new List<TrafficSignalObservation>();
        foreach(var signal in signals)
        {
            if(signal.type!=ETS2LA.Game.SDK.SemaphoreType.TRAFFICLIGHT)continue;
            var position=signal.GetWorldCoordinates();
            // The pinned host stores wire qw,qx,qy,qz in Quaternion's X,Y,Z,W fields.
            var q=signal.rotation;var rotation=new Quaternion(q.Y,q.Z,q.W,q.X);
            if(!Finite(position)||!float.IsFinite(rotation.LengthSquared())||rotation.LengthSquared()<.001f
                ||!float.IsFinite(signal.time_remaining)||signal.time_remaining<0
                ||!Enum.IsDefined((SignalPhase)signal.state))continue;
            copy.Add(new(signal.id,position,Quaternion.Normalize(rotation),(SignalPhase)signal.state,signal.time_remaining));
        }
        return new(receivedAt,Array.AsReadOnly(copy.ToArray()));
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
}
