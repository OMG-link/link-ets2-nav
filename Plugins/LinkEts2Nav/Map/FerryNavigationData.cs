using System.Numerics;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.Map;

/// <summary>A physical port instance, not a nearest-city guess.</summary>
public sealed record FerryPort(string Id,string Name,ulong ItemUid,ulong NodeUid,Vector3 Position,
    ulong PrefabUid,IReadOnlyList<ulong> Interfaces,bool Train);

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
    public bool AtDeparture(VehicleSnapshot pose)=>Contains(DepartureAreas,Leg.Source,pose);
    public bool AtArrival(VehicleSnapshot pose)=>Contains(ArrivalAreas,Leg.Target,pose);
    private static bool Contains(IReadOnlyList<FreeNavigationArea> areas,FerryPort port,VehicleSnapshot pose)
    {
        var position=new Vector3((float)pose.X,(float)pose.Y,(float)pose.Z);
        if(areas.Count>0)return areas.Any(a=>a.Contains(position));
        // If the destination PPD is not loaded yet, SDK event + exact planned
        // port identity still require a landing near its actual activation node.
        return Math.Abs(position.Y-port.Position.Y)<=3&&Vector2.Distance(new(position.X,position.Z),new(port.Position.X,port.Position.Z))<=75;
    }
}
