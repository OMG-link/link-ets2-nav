using System.Numerics;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Core;

namespace LinkEts2Nav.Map;

/// <summary>A physical port instance, not a nearest-city guess.</summary>
public sealed record FerryPort(string Id,string Name,ulong ItemUid,ulong NodeUid,Vector3 Position,
    ulong PrefabUid,IReadOnlyList<ulong> Interfaces,bool Train)
{
    public IReadOnlyDictionary<ulong,FerryEntrance> Entrances {get;init;}=new Dictionary<ulong,FerryEntrance>();
    public Vector3 UnloadPosition {get;init;}=Position;
}

/// <summary>Ordered official navigation records explicitly select this transport connection.</summary>
public sealed record FerryRouteLeg(FerryPort Source,FerryPort Target,ulong EntranceUid,ulong ExitUid,ulong? GoalUid)
{
    public string Id=>$"ferry:{Source.NodeUid:X16}:{Target.NodeUid:X16}:{EntranceUid:X16}";
    public bool Matches(string source,string target)=>Source.Id==source&&Target.Id==target;
}

/// <summary>Registered only after physically entering the planned departure port.</summary>
public sealed record FerryTravelExpectation(FerryRouteLeg Leg,DateTimeOffset EnteredAt,
    IReadOnlyList<FreeNavigationArea> DepartureAreas,IReadOnlyList<FreeNavigationArea> ArrivalAreas)
{
    public bool AtDeparture(VehicleSnapshot pose)=>InArea(DepartureAreas,pose)||
        Leg.Source.Entrances.TryGetValue(Leg.EntranceUid,out var entrance)&&entrance.IsInside(pose);
    public bool AtArrival(VehicleSnapshot pose)=>InArea(ArrivalAreas,pose)||Near(Leg.Target.UnloadPosition,pose,20);
    private static bool InArea(IReadOnlyList<FreeNavigationArea> areas,VehicleSnapshot pose)
    {
        var position=new Vector3((float)pose.X,(float)pose.Y,(float)pose.Z);
        return areas.Any(a=>a.Contains(position));
    }
    private static bool Near(Vector3 point,VehicleSnapshot pose,double tolerance)
        =>Math.Abs(pose.Y-point.Y)<=3&&Vector2.Distance(new((float)pose.X,(float)pose.Z),new(point.X,point.Z))<=tolerance;
}

/// <summary>A finite interface plane, oriented by the native adjoining road endpoint tangent.</summary>
public sealed record FerryEntrance(Vector3 Position,Vector3 Inward,double HalfWidth,DirectedRoad Approach)
{
    private Vector3 Direction=>Vector3.Normalize(new(Inward.X,0,Inward.Z));
    private static Vector3 Point(VehicleSnapshot p)=>new((float)p.X,(float)p.Y,(float)p.Z);
    public double Signed(VehicleSnapshot p)=>Vector3.Dot(Point(p)-Position,Direction);
    public bool IsInside(VehicleSnapshot p)=>Signed(p)>=0&&Math.Abs(p.Y-Position.Y)<=3;
    public bool Crossed(VehicleSnapshot a,VehicleSnapshot b,bool incoming)
    {
        double seconds=(b.ReceivedAt-a.ReceivedAt).TotalSeconds;
        if(seconds<=0||seconds>2||a.SdkActive!=b.SdkActive||b.SimulationTime<a.SimulationTime)return false;
        if(Vector3.Distance(Point(a),Point(b))>Math.Max(20,Math.Max(a.SpeedMetersPerSecond,b.SpeedMetersPerSecond)*seconds+10))return false;
        double from=Signed(a),to=Signed(b);
        if(incoming?from>0||to<0:from<0||to>0)return false;
        if(Math.Abs(to-from)<.0001)return false;
        var cross=Vector3.Lerp(Point(a),Point(b),(float)(-from/(to-from)));
        var lateral=Vector3.Cross(Direction,Vector3.UnitY);
        if(Math.Abs(Vector3.Dot(cross-Position,lateral))>HalfWidth+1||Math.Abs(cross.Y-Position.Y)>3)return false;
        return !incoming||new DirectedPathMatcher(HalfWidth+1).ProjectDistance(Point(a),a.ForwardDirection,[Approach]) is not null;
    }
}
