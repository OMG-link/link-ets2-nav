using System.Numerics;
using TruckLib.ScsMap;
using ScsMap = TruckLib.ScsMap.Map;
namespace LinkEts2Nav.Map;

/// <summary>Area ownership is by source sector, not every sector touched by its polygon.</summary>
public sealed class LimitRegionIndex
{
    private readonly IReadOnlyDictionary<SectorCoordinate, LimitRegion[]> sectors;
    public LimitRegionIndex(IEnumerable<LimitRegion> regions)
    {
        sectors = regions.GroupBy(r => r.OwnerSector ?? Owner(r.Points))
            .ToDictionary(g => g.Key, g => g.ToArray());
    }
    internal static SectorCoordinate Owner(IReadOnlyList<Vector3> points)
        => ScsMap.GetSectorOfCoordinate(points.Count == 0 ? Vector3.Zero : points.Aggregate(Vector3.Zero,(a,b)=>a+b)/points.Count);

    // The user-defined coverage contract: all relevant areas belong to these four sectors.
    public static IReadOnlyList<SectorCoordinate> NearestFour(Vector3 point)
    {
        var at = ScsMap.GetSectorOfCoordinate(point);
        int dx = point.X - at.X * ScsMap.SectorSize < ScsMap.SectorSize / 2 ? -1 : 1;
        int dz = point.Z - at.Z * ScsMap.SectorSize < ScsMap.SectorSize / 2 ? -1 : 1;
        return [at, new(at.X + dx, at.Z), new(at.X, at.Z + dz), new(at.X + dx, at.Z + dz)];
    }
    public IReadOnlyList<LimitRegion> At(Vector3 point)
        => NearestFour(point).SelectMany(s => sectors.GetValueOrDefault(s) ?? [])
            .Where(r => r.Contains(point)).DistinctBy(r => r.Id).ToArray();
}
