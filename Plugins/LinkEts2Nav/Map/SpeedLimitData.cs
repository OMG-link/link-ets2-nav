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
    public string? CityName { get; init; }
    public string? RoadId { get; init; }
    public bool OwnershipAmbiguous { get; init; }
}
public sealed record LimitRoad(DirectedRoad Geometry, string? SpeedClass)
{
    public bool SingleTrack { get; init; }
    public bool ForwardTraffic { get; init; } = true;
    public bool ReverseTraffic { get; init; } = true;
    public DirectionalSpeedClasses? DirectionalClasses { get; init; }
    public string? ClassFor(DirectedRoad directed) => DirectionalClasses is null?SpeedClass:
        directed.StartNodeUid==Geometry.StartNodeUid&&directed.EndNodeUid==Geometry.EndNodeUid?DirectionalClasses.Forward:
        directed.StartNodeUid==Geometry.EndNodeUid&&directed.EndNodeUid==Geometry.StartNodeUid?DirectionalClasses.Reverse:null;
    private string? country;
    private Func<MapQueryResult<string>>? countryQuery;
    // Explicit country metadata remains useful to callers constructing their own
    // LimitRoad values. Loaded game roads provide the native query instead.
    public string? Country { get=>country; init {country=value;countryQuery=null;} }
    internal Func<MapQueryResult<string>>? CountryQuery { init=>countryQuery=value; }
    public bool CountryUncertain { get; init; }
    public MapQueryResult<string> QueryCountry()
    {
        if(countryQuery is not null)return countryQuery();
        if(CountryUncertain)return MapQueryResult<string>.Unavailable(new UndeterminedRoadCountry($"Road {Geometry.Id}: declared country evidence conflicts."));
        return country is not null?MapQueryResult<string>.Ready(country):MapQueryResult<string>.Unavailable(
            new MapQueryFailure(MapQueryFailureKind.DefinitionUnavailable,$"Road {Geometry.Id}: country evidence is unavailable."));
    }
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

    // On a straight road segment the polygon and its vertical range can only change
    // membership at these fractions. Include the full height of elevated traffic areas.
    internal void AddBoundaryCrossings(Vector3 from, Vector3 to, ref List<double>? crossings)
    {
        if (Points.Count < 3 || Math.Max(from.X,to.X)<minX || Math.Min(from.X,to.X)>maxX
            || Math.Max(from.Z,to.Z)<minZ || Math.Min(from.Z,to.Z)>maxZ) return;
        var delta=to-from;
        if(Height>0)
        {
            var floor=Points[0].Y;
            if(Math.Max(from.Y,to.Y)<floor||Math.Min(from.Y,to.Y)>floor+Height)return;
            if(delta.Y!=0)
            {
                Add((floor-from.Y)/delta.Y,ref crossings);
                Add((floor+Height-from.Y)/delta.Y,ref crossings);
            }
        }
        for(int i=0,j=Points.Count-1;i<Points.Count;j=i++)
        {
            var a=Points[j];var edge=Points[i]-a;
            double denominator=(double)delta.X*edge.Z-(double)delta.Z*edge.X;
            if(denominator==0)continue;
            var offset=a-from;
            double onEdge=((double)offset.X*delta.Z-(double)offset.Z*delta.X)/denominator;
            if(onEdge is >=0 and <=1)
                Add(((double)offset.X*edge.Z-(double)offset.Z*edge.X)/denominator,ref crossings);
        }
        static void Add(double t,ref List<double>? result)
        {
            if(t is >0 and <1)(result??=[]).Add(t);
        }
    }

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
    public ProjectedSignIndex? SignProjections { get; init; }
    public LimitRegionIndex? RegionIndex { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public static SpeedLimitData Empty { get; } = new(new Dictionary<ulong,LimitRoad>(),[],[],
        new Dictionary<string,TruckSpeedLimit>(),null,"限速资源尚未加载");
}
public sealed record SpeedLimitResult(int? Kph, string Reason)
{
    public MapQueryIssue? MapIssue {get;init;}
}
