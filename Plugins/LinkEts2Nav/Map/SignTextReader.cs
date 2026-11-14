using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

/// <summary>Reads fixed rules and visible text from the actual template/board bindings.</summary>
public sealed class SignTextReader(IFileSystem fs)
{
    private Dictionary<string,string>? templates,boards;
    private readonly Dictionary<string,Unit[]> files=new(StringComparer.Ordinal);
    public IReadOnlyList<string> ReadTexts(Sign sign)
    {
        if(string.IsNullOrWhiteSpace(sign.SignTemplate))return [];
        try
        {
            templates??=Index("templates");boards??=Index("boards");
            if(!templates.TryGetValue(sign.SignTemplate,out var path))return [];
            var units=Units(path);var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var binding in units.Where(u=>u.Class=="sign_template_board_binding"))
            {
                string area=Text(binding,"area_name"),board=Text(binding,"linked_board");
                var replacement=sign.BoardOverrides?.LastOrDefault(o=>o.AreaName.ToString()==area&&!string.IsNullOrEmpty(o.Board.ToString()));
                if(replacement is not null)board="sign_board."+replacement.Board;
                if(!boards.TryGetValue(board,out var boardPath))continue;
                foreach(var text in Units(boardPath).Where(u=>u.Class=="sign_template_text"))
                {
                    if(!uint.TryParse(Text(text,"id"),out uint id))continue;
                    string value=Text(text,"text");
                    foreach(var over in sign.SignOverrides??[])
                        if(over.Id==id&&over.AreaName.ToString()==area)
                            foreach(var attribute in over.Attributes.Where(a=>a.Index==0&&a.Type==typeof(string)))
                                value=(string)attribute.GetValue();
                    value=Regex.Replace(value.Trim(),@"\s+"," ").Normalize();
                    if(value.Length is <1 or >128||value is "Name" or "City" or "Text"||value.StartsWith("@@",StringComparison.Ordinal)
                        ||!value.Any(char.IsLetter)||value.Any(c=>char.IsControl(c)||c is '<' or '>' or '[' or ']' or '{' or '}'))continue;
                    names.Add(value);
                }
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        catch(Exception e) when(e is IOException or InvalidDataException or FormatException or ArgumentException)
        {return [];}
    }
    /// <summary>A fixed template rule can corroborate a combined city-end/numeric model rule.</summary>
    public string? TrafficRule(Sign sign)
    {
        if(string.IsNullOrWhiteSpace(sign.SignTemplate))return null;
        try
        {
            templates??=Index("templates");
            if(!templates.TryGetValue(sign.SignTemplate,out var path))return null;
            var root=Units(path).Where(u=>u.Class=="sign_template").ToArray();
            if(root.Length!=1||Text(root[0],"dynamic_attributes") is not ("" or "0"))return null;
            return Text(root[0],"traffic_rule");
        }
        catch(Exception e) when(e is IOException or InvalidDataException or FormatException or ArgumentException)
        {return null;}
    }

    private Unit[] Units(string path)
    {
        if(!files.TryGetValue(path,out var units))files[path]=units=SiiFile.Open(path,fs).Units.ToArray();
        return units;
    }
    private Dictionary<string,string> Index(string prefix)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var path in fs.GetFiles("/def/sign/").Where(p=>fs.GetParent(p).TrimEnd('/')=="/def/sign"
            &&Path.GetFileName(p).StartsWith(prefix,StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            foreach(var unit in Units(path))
                if(unit.Attributes.ContainsKey("path"))result[unit.Name]=Text(unit,"path");
        return result;
    }
    private static string Text(Unit unit,string key)
    {
        if(!unit.Attributes.TryGetValue(key,out var value)||value is null)return "";
        object boxed=value;
        if(boxed is Token token)return token.ToString();
        var inner=boxed.GetType().GetProperty("Value");
        return Convert.ToString(inner is null?boxed:inner.GetValue(boxed),CultureInfo.InvariantCulture)?.Trim('"')??"";
    }
}
