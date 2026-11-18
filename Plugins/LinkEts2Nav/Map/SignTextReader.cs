using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using TruckLib;
using TruckLib.ScsMap;
using TruckLib.Sii;
namespace LinkEts2Nav.Map;

/// <summary>Reads fixed rules and visible text from the actual template/board bindings.</summary>
public sealed class SignTextReader(GameDataManager data)
{
    public sealed record Diagnostic(string Reason,string Resource,string? Detail=null);
    public IReadOnlyList<Diagnostic> Diagnostics=>diagnostics;
    private readonly List<Diagnostic> diagnostics=[];
    public SignTextReader(IFileSystem fs):this(GameDataManager.From(fs)) { }
    private IReadOnlyDictionary<string,string>? templates,boards;
    public IReadOnlyList<string> ReadTexts(Sign sign)=>Read(sign,false);
    public IReadOnlyList<string> ReadRawTexts(Sign sign)=>Read(sign,true);
    /// <summary>Offline inventory also records literal text in mileage objects; it does not resolve targets or change city naming.</summary>
    public IReadOnlyList<string> ReadInventoryTexts(Sign sign)=>Read(sign,true,true);
    private IReadOnlyList<string> Read(Sign sign,bool raw,bool mileage=false)
    {
        diagnostics.Clear();
        if(string.IsNullOrWhiteSpace(sign.SignTemplate)){diagnostics.Add(new("NoTextTemplate",sign.Model.ToString()));return [];}
        templates??=Index("templates");boards??=Index("boards");
        if(!templates.TryGetValue(sign.SignTemplate,out var path)){diagnostics.Add(new("TemplateNotIndexed",sign.SignTemplate));return [];}
        var units=data.GetSii(path);var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var binding in units.OfClass("sign_template_board_binding"))
        {
            string area=Text(binding,"area_name"),board=Text(binding,"linked_board");
            var replacement=sign.BoardOverrides?.LastOrDefault(o=>o.AreaName.ToString()==area&&!string.IsNullOrEmpty(o.Board.ToString()));
            if(replacement is not null)board="sign_board."+replacement.Board;
            if(!boards.TryGetValue(board,out var boardPath)){diagnostics.Add(new("BoardNotIndexed",board,area));continue;}
            foreach(var text in data.GetSii(boardPath).Units.Where(u=>u.Class=="sign_template_text"||mileage&&u.Class=="sign_template_mileage"))
            {
                if(!uint.TryParse(Text(text,"id"),out uint id))continue;
                string value=Text(text,"text");
                foreach(var over in sign.SignOverrides??[])
                    if(over.Id==id&&over.AreaName.ToString()==area)
                        foreach(var attribute in over.Attributes.Where(a=>a.Index==0&&a.Type==typeof(string)))
                            value=(string)attribute.GetValue();
                if(!raw&&text.Class=="sign_template_text"&&!VisibleText(text,sign,area,id))continue;
                value=Regex.Replace(value.Trim(),@"\s+"," ").Normalize();
                if(!raw&&(value.Length is <1 or >128||value is "Name" or "City" or "Text"||value.StartsWith("@@",StringComparison.Ordinal)
                    ||!value.Any(char.IsLetter)||value.Any(c=>char.IsControl(c)||c is '<' or '>' or '[' or ']' or '{' or '}')))continue;
                names.Add(value);
            }
        }
        if(names.Count==0)diagnostics.Add(new("NoReadableTextObjects",path));
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A fixed template rule can corroborate a combined city-end/numeric model rule.</summary>
    public string? TrafficRule(Sign sign)
    {
        if(string.IsNullOrWhiteSpace(sign.SignTemplate))return null;
        templates??=Index("templates");
        if(!templates.TryGetValue(sign.SignTemplate,out var path))return null;
        var root=data.GetSii(path).OfClass("sign_template").ToArray();
        if(root.Length!=1||Text(root[0],"dynamic_attributes") is not ("" or "0"))return null;
        return Text(root[0],"traffic_rule");
    }

    // Native sign_template_text overrides use: text=0, face=1, size=2, color=3,
    // font_opacity=4. This is not the schema of images, frames or mileage objects.
    private bool VisibleText(Unit text,Sign sign,string area,uint id)
    {
        string configured=Text(text,"font_opacity");
        float opacity=1;
        if(configured.Length>0&&!float.TryParse(configured,NumberStyles.Float,CultureInfo.InvariantCulture,out opacity))
        {diagnostics.Add(new("UnknownTextOpacity",sign.SignTemplate,$"{area}:{id} font_opacity={configured}"));return false;}
        foreach(var over in sign.SignOverrides??[])
            if(over.Id==id&&over.AreaName.ToString()==area)
                foreach(var attribute in over.Attributes.Where(a=>a.Index==4))
                {
                    if(attribute.Type!=typeof(float))
                    {diagnostics.Add(new("UnknownTextOpacity",sign.SignTemplate,$"{area}:{id} override type={attribute.Type.Name}"));return false;}
                    opacity=(float)attribute.GetValue();
                }
        if(!float.IsFinite(opacity))
        {diagnostics.Add(new("UnknownTextOpacity",sign.SignTemplate,$"{area}:{id} non-finite opacity"));return false;}
        return opacity>0;
    }

    private IReadOnlyDictionary<string,string> Index(string prefix)=>data.SignIndex(prefix);
    private static string Text(Unit unit,string key)
    {
        if(!unit.Attributes.TryGetValue(key,out var value)||value is null)return "";
        object boxed=value;
        if(boxed is Token token)return token.ToString();
        var inner=boxed.GetType().GetProperty("Value");
        return Convert.ToString(inner is null?boxed:inner.GetValue(boxed),CultureInfo.InvariantCulture)?.Trim('"')??"";
    }
}
