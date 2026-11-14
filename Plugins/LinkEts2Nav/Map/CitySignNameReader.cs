using TruckLib;
using TruckLib.ScsMap;
namespace LinkEts2Nav.Map;

/// <summary>City names require a unique readable board text; general inventories retain every text.</summary>
internal sealed class CitySignNameReader(IFileSystem fs)
{
    private readonly SignTextReader reader=new(fs);
    public string? Read(Sign sign)
    {
        var names=reader.ReadTexts(sign);
        return names.Count==1?names[0]:null;
    }
    public string? TrafficRule(Sign sign)=>reader.TrafficRule(sign);
}
