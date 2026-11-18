namespace LinkEts2Nav.Map;

/// <summary>Shared country/class lookup for journey estimates and camera-path simulation.</summary>
public static class TruckSpeedLimitRules
{
    // Conservative lower bounds of the installed country truck rules in km/h.
    // UK imperial values are normalized only for choosing this shared table.
    private static readonly IReadOnlyDictionary<string,TruckSpeedLimit> UnknownCountryDefaults=new Dictionary<string,TruckSpeedLimit>(StringComparer.Ordinal)
    {
        ["motorway"]=new(70,50,70),["expressway"]=new(70,40,70),
        ["local_road"]=new(60,40,60),["slow_road"]=new(30,30,30)
    };
    public static bool TryGet(IReadOnlyDictionary<string,TruckSpeedLimit> limits,string? country,string speedClass,
        out TruckSpeedLimit rule,out bool usedDefault)
    {
        usedDefault=false;
        if(country is null)
        {
            usedDefault=UnknownCountryDefaults.TryGetValue(speedClass,out rule!);
            return usedDefault;
        }
        if(limits.TryGetValue(country+"/"+speedClass,out rule!))return true;
        if(!string.IsNullOrWhiteSpace(country)&&speedClass=="slow_road")
        {
            rule=new(30,30,30);usedDefault=true;return true;
        }
        rule=null!;return false;
    }
    public static string DefaultReasonFor(string? country,string speedClass)
    {
        if(country is not null)return DefaultReason;
        var rule=UnknownCountryDefaults[speedClass];
        return $"国家无法唯一确定，采用 {speedClass} 默认限速：非城区 {rule.Rural}、城区 {rule.Urban} km/h";
    }
    public const string DefaultReason="slow_road 未定义，采用默认 30 km/h";
}
