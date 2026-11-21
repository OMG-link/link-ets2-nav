using System.Numerics;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace LinkEts2Nav.Map;

public sealed record TrafficSignalLamp(Vector3 Position,Quaternion Rotation);
public sealed record TrafficSignalControl(ulong PrefabUid,int CurveIndex,int SignalId,Vector3 StopPoint,Vector3 Forward,
    IReadOnlyList<TrafficSignalLamp> Lamps)
{
    public string Id=>$"{PrefabUid:X16}:{SignalId}";
}

/// <summary>Native controlled curves and physical lamp poses. The SDK establishes the live lamp type and phase.</summary>
public sealed class TrafficSignalMap(IEnumerable<TrafficSignalControl> controls)
{
    private readonly IReadOnlyDictionary<(ulong,int),TrafficSignalControl> curves=controls.ToDictionary(c=>(c.PrefabUid,c.CurveIndex));
    public static TrafficSignalMap Empty {get;}=new([]);
    public TrafficSignalControl? Find(ulong prefab,int curve)=>curves.GetValueOrDefault((prefab,curve));
    public int Count=>curves.Count;
    public static IReadOnlyList<TrafficSignalControl> Build(Prefab prefab,PrefabDescriptor descriptor)
        => Build(prefab,descriptor,null);

    internal static IReadOnlyList<TrafficSignalControl> Build(Prefab prefab,PrefabPlayerProjection projection)
        => Build(prefab,projection.Descriptor,projection.AllowedCurves);

    private static IReadOnlyList<TrafficSignalControl> Build(Prefab prefab,PrefabDescriptor descriptor,
        IReadOnlyList<bool>? allowedCurves)
    {
        if(!PrefabPlacement.TryCreate(prefab,descriptor,out var placement))return [];
        var groups=new Dictionary<int,TrafficSignalLamp[]>();
        var z=placement.ToWorldDirection(Vector3.UnitZ);var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.Atan2(z.X,z.Z));
        var result=new List<TrafficSignalControl>();
        for(int index=0;index<descriptor.NavCurves.Count;index++)
        {
            var curve=descriptor.NavCurves[index];if(curve.SemaphoreId<0||allowedCurves?[index]==false)continue;
            if(!groups.TryGetValue(curve.SemaphoreId,out var lamps))
            {
                // UseProfile, country variants and visual heads do not decide eligibility.
                // Only a pose-bound SDK TRAFFICLIGHT can satisfy the runtime query.
                lamps=descriptor.Semaphores.Where(s=>s.SemaphoreId==(uint)curve.SemaphoreId)
                    .Select(s=>new TrafficSignalLamp(placement.ToWorld(s.Position),Quaternion.Normalize(rotation*s.Rotation))).ToArray();
                groups[curve.SemaphoreId]=lamps;
            }
            var forward=placement.ToWorldDirection(Vector3.Transform(-Vector3.UnitZ,curve.StartRotation));forward.Y=0;
            if(forward.LengthSquared()<.001f)continue;
            result.Add(new(prefab.Uid,index,curve.SemaphoreId,placement.ToWorld(curve.StartPosition),Vector3.Normalize(forward),lamps));
        }
        return result;
    }
}
