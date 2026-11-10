using System.Numerics;
using TruckLib;
using TruckLib.ScsMap;
using ScsMap = TruckLib.ScsMap.Map;
namespace LinkEts2Nav.Map;

/// <summary>Background-worker-only cache. Area-only reads never start camera/prefab processing.</summary>
internal sealed class SpeedLimitAreaCache(IFileSystem fs, string mapPath)
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
            var supplement = new AreaMap();
            supplement.Read(mapPath, fs, missing);
            var extra = reader.ReadRegions(supplement);
            foreach (var s in missing) cache[s] = extra.Where(r => r.OwnerSector == s).ToArray();
        }
        token.ThrowIfCancellationRequested();
        return required.SelectMany(s => cache.GetValueOrDefault(s) ?? []).DistinctBy(r => r.Id).ToArray();
    }
    private sealed class AreaMap : ScsMap
    {
        protected override bool PostProcessItem(MapItem item) => item is CityArea or TrafficArea;
    }
}
