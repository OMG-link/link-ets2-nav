using System.Numerics;
using TruckLib.ScsMap;
using ScsMap = TruckLib.ScsMap.Map;
namespace LinkEts2Nav.Map;

/// <summary>Background-worker-only region projection; supplementary sectors come from the dataset.</summary>
internal sealed class SpeedLimitAreaCache(GameDataManager data)
{
    private readonly Dictionary<SectorCoordinate, IReadOnlyList<LimitRegion>> cache = [];
    public IReadOnlyList<LimitRegion> Update(ScsMap loaded, IEnumerable<SectorCoordinate> loadedSectors,
        IEnumerable<Vector3> queryGeometry, SpeedLimitDefinitionReader reader, CancellationToken token)
    {
        var regions = reader.ReadRegions(loaded);
        foreach (var s in loadedSectors)
            cache[s] = regions.Where(r => r.OwnerSector == s).ToArray();
        var required = queryGeometry.SelectMany(LimitRegionIndex.NearestFour).Distinct().ToArray();
        var missing = required.Where(s => !cache.ContainsKey(s)).ToArray();
        if (missing.Length > 0)
        {
            token.ThrowIfCancellationRequested();
            // Missing sea sectors are empty and cached too. Map.Read filters absent files.
            var supplement=data.ReadWindow(missing);
            var extra = reader.ReadRegions(supplement);
            foreach (var s in missing) cache[s] = extra.Where(r => r.OwnerSector == s).ToArray();
        }
        token.ThrowIfCancellationRequested();
        return required.SelectMany(s => cache.GetValueOrDefault(s) ?? []).DistinctBy(r => r.Id).ToArray();
    }
}
