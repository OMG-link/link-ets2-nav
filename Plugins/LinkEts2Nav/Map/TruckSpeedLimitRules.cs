namespace LinkEts2Nav.Map;

/// <summary>Shared country/class lookup for journey estimates and camera-path simulation.</summary>
public static class TruckSpeedLimitRules
{
    public static bool TryGet(IReadOnlyDictionary<string,TruckSpeedLimit> limits,string country,string speedClass,
        out TruckSpeedLimit rule,out bool usedDefault)
    {
        usedDefault=false;
        if(limits.TryGetValue(country+"/"+speedClass,out rule!))return true;
        if(!string.IsNullOrWhiteSpace(country)&&speedClass=="slow_road")
        {
            rule=new(30,30,30);usedDefault=true;return true;
        }
        rule=null!;return false;
    }
    public const string DefaultReason="slow_road 未定义，采用默认 30 km/h";
}
