using System.Collections;
using System.Globalization;
using TruckLib;
using TruckLib.Sii;

namespace LinkEts2Nav.Map;

/// <summary>Only the explicit actor_access player flag establishes a prohibition.</summary>
public sealed class PlayerAccessRules(IReadOnlyDictionary<string,Unit> rules)
{
    public bool? Read(IEnumerable<string> names)
    {
        var flags=new HashSet<bool>();var seen=new HashSet<string>(StringComparer.Ordinal);
        if(!names.All(name=>Visit(name,seen,flags))||flags.Count!=1)return null;
        return flags.Single();
    }
    public bool? Read(string name)=>string.IsNullOrEmpty(name)?null:Read([name]);
    private bool Visit(string name,HashSet<string> seen,HashSet<bool> flags)
    {
        if(!seen.Add(name))return true;
        if(!rules.TryGetValue(name,out var rule))return false;
        if(Text(rule,"rule")=="actor_access")
        {
            var numbers=Values(rule,"num_params").ToArray();
            if(numbers.Length<5||!double.TryParse(Value(numbers[4]),NumberStyles.Float,CultureInfo.InvariantCulture,out double player)
                ||player is not (0 or 1))return false;
            flags.Add(player==1);
        }
        return Values(rule,"additional_rules").Select(Value).All(child=>Visit(child,seen,flags));
    }
    private static IEnumerable<object> Values(Unit unit,string key)
        =>unit.Attributes.TryGetValue(key,out var value)&&value is IEnumerable list&&value is not string?list.Cast<object>():[];
    private static string Text(Unit unit,string key)=>unit.Attributes.TryGetValue(key,out var value)?Value(value):"";
    private static string Value(object? value)=>value is null?"":Convert.ToString(value is Token token?token.ToString():
        value.GetType().GetProperty("Value")?.GetValue(value)??value,CultureInfo.InvariantCulture)?.Trim('"')??"";
}
