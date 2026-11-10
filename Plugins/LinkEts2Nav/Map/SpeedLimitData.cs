using System.Numerics;
using LinkEts2Nav.Core;
namespace LinkEts2Nav.Map;

public sealed record LimitAction(int? SetKph = null, bool Reset = false, bool? Urban = null, bool Unsupported = false, bool? Motorway = null)
{
    public bool Relevant => SetKph.HasValue || Reset || Urban.HasValue || Motorway.HasValue || Unsupported;
}
public sealed record LimitSign(string Id, Vector3 Position, Vector3 Direction, bool FollowRoad,
    LimitAction Action, bool TemplateOverride = false)
{
    public string? RoadId { get; init; }
    public bool OwnershipAmbiguous { get; init; }
}
public sealed record LimitRoad(DirectedRoad Geometry, string? SpeedClass)
{
    public IReadOnlyList<PostedLimitInterval> ForwardSignLimits { get; init; } = [];
    public IReadOnlyList<PostedLimitInterval> ReverseSignLimits { get; init; } = [];
    public bool ForwardTraffic { get; init; } = true;
    public bool ReverseTraffic { get; init; } = true;
    public DirectionalSpeedClasses? DirectionalClasses { get; init; }
    public string? ClassFor(DirectedRoad directed) => DirectionalClasses is null?SpeedClass:
        directed.StartNodeUid==Geometry.StartNodeUid&&directed.EndNodeUid==Geometry.EndNodeUid?DirectionalClasses.Forward:
        directed.StartNodeUid==Geometry.EndNodeUid&&directed.EndNodeUid==Geometry.StartNodeUid?DirectionalClasses.Reverse:null;
    public string? Country { get; init; }
    public bool CountryUncertain { get; init; }
}
public sealed record DirectionalSpeedClasses(string? Forward,string? Reverse);
public sealed record LimitRegion(string Id, IReadOnlyList<Vector3> Points, float Height, bool Urban, int? SetKph = null)
{
    public string? Country { get; init; }
    public TruckLib.ScsMap.SectorCoordinate? OwnerSector { get; init; }
    public bool Unsupported { get; init; }
    private readonly float minX = Points.Count == 0 ? 0 : Points.Min(p => p.X);
    private readonly float maxX = Points.Count == 0 ? 0 : Points.Max(p => p.X);
    private readonly float minZ = Points.Count == 0 ? 0 : Points.Min(p => p.Z);
    private readonly float maxZ = Points.Count == 0 ? 0 : Points.Max(p => p.Z);

    public bool Contains(Vector3 p)
    {
        if (Points.Count < 3 || p.X < minX || p.X > maxX || p.Z < minZ || p.Z > maxZ) return false;
        if (Height > 0 && (p.Y < Points[0].Y || p.Y > Points[0].Y + Height)) return false;
        bool inside = false;
        for (int i=0,j=Points.Count-1;i<Points.Count;j=i++)
        {
            var a=Points[i]; var b=Points[j];
            if ((a.Z>p.Z)!=(b.Z>p.Z) && p.X<(b.X-a.X)*(p.Z-a.Z)/(b.Z-a.Z)+a.X) inside=!inside;
        }
        return inside;
    }
}
public sealed record TruckSpeedLimit(int Rural, int Urban, int Maximum);
public sealed record SpeedLimitData(
    IReadOnlyDictionary<ulong,LimitRoad> Roads, IReadOnlyList<LimitSign> Signs,
    IReadOnlyList<LimitRegion> Regions, IReadOnlyDictionary<string,TruckSpeedLimit> Limits,
    string? Country, string? Error = null)
{
    public PostedLimitIndex? PostedLimits { get; init; }
    public LimitRegionIndex? RegionIndex { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public static SpeedLimitData Empty { get; } = new(new Dictionary<ulong,LimitRoad>(),[],[],
        new Dictionary<string,TruckSpeedLimit>(),null,"限速资源尚未加载");
}
public sealed record SpeedLimitResult(int? Kph, string Reason);

/// <summary>Null Kph means no posted limit; Unknown is a distinct unresolved/conflicting state.</summary>
public sealed record PostedLimitState(int? Kph = null, bool? Urban = null, bool? Motorway = null, bool Unknown = false, bool NumericKnown = true);
public sealed record PostedLimitInterval(double FromMeters, double ToMeters, PostedLimitState State);
