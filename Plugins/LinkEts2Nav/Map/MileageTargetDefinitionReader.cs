using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using TruckLib;
using TruckLib.Sii;

namespace LinkEts2Nav.Map;

public sealed record MileageNameVariant(string Token,string RawText,string? Text);
public sealed record MileageTargetDefinition(string Id,string Source,string RawDefaultName,string? DefaultName,
    IReadOnlyList<MileageNameVariant> Variants,ulong? NodeUid,Vector3? Position,float? SearchRadius);

/// <summary>Offline name/target metadata. A target node is a destination, never an owning local road or sign.</summary>
public sealed class MileageTargetDefinitionReader(IFileSystem fs)
{
    public List<string> Diagnostics {get;}=[];
    public IReadOnlyList<MileageTargetDefinition> Read()
    {
        Diagnostics.Clear();var result=new Dictionary<string,MileageTargetDefinition>(StringComparer.Ordinal);
        foreach(string file in fs.GetFiles("/def/sign/").Where(p=>fs.GetParent(p).TrimEnd('/')=="/def/sign"
            &&Path.GetFileName(p).StartsWith("mileage_targets",StringComparison.Ordinal)&&p.EndsWith(".sii",StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        try
        {
            foreach(var unit in SiiFile.Open(file,fs).Units.Where(u=>u.Class=="mileage_target"))
            {
                string raw=Text(unit,"default_name");string? name=Name(raw,unit.Name);
                var tokens=Array(unit,"variants");var texts=Array(unit,"names");
                if(tokens.Length!=texts.Length)Diagnostics.Add(file+" "+unit.Name+": VariantNameCountMismatch");
                var variants=texts.Select((t,i)=>new MileageNameVariant(i<tokens.Length?tokens[i]:"",t,Name(t,unit.Name))).ToArray();
                ulong? uid=ulong.TryParse(Text(unit,"node_uid"),NumberStyles.None,CultureInfo.InvariantCulture,out ulong parsed)&&parsed!=0?parsed:null;
                Vector3? position=null;
                object? positionValue=unit.Attributes.GetValueOrDefault("position");
                object? unwrapped=positionValue?.GetType().GetProperty("Value")?.GetValue(positionValue)??positionValue;
                if(unwrapped is ValueTuple<object,object,object> tuple
                    &&float.TryParse(Value(tuple.Item1),NumberStyles.Float,CultureInfo.InvariantCulture,out float x)
                    &&float.TryParse(Value(tuple.Item2),NumberStyles.Float,CultureInfo.InvariantCulture,out float y)
                    &&float.TryParse(Value(tuple.Item3),NumberStyles.Float,CultureInfo.InvariantCulture,out float z))unwrapped=new Vector3(x,y,z);
                if(unwrapped is Vector3 vector
                    &&float.IsFinite(vector.X)&&float.IsFinite(vector.Y)&&float.IsFinite(vector.Z)
                    &&Math.Abs(vector.X)<float.MaxValue/2&&Math.Abs(vector.Z)<float.MaxValue/2)position=vector;
                float? radius=float.TryParse(Text(unit,"search_radius"),NumberStyles.Float,CultureInfo.InvariantCulture,out float r)&&float.IsFinite(r)?r:null;
                if(uid is null&&position is null)Diagnostics.Add(unit.Name+": TargetLocationUnavailable");
                result[unit.Name]=new(unit.Name,file,raw,name,variants,uid,position,radius);
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException or FormatException or ArgumentException)
        {Diagnostics.Add(file+": "+e.GetType().Name+": "+e.Message);}
        return result.Values.OrderBy(t=>t.Id,StringComparer.Ordinal).ToArray();
    }

    private string? Name(string raw,string id)
    {
        if(string.IsNullOrWhiteSpace(raw))return null;
        string text;
        try
        {
            // TruckLib keeps escaped UTF-8 bytes as string literals. Match one or two
            // backslashes; do not interpret arbitrary escape sequences as another language.
            text=Regex.Replace(raw,@"(?:\\{1,2}x[0-9a-fA-F]{2})+",match=>
                new UTF8Encoding(false,true).GetString(Regex.Matches(match.Value,@"x([0-9a-fA-F]{2})")
                    .Select(m=>Convert.ToByte(m.Groups[1].Value,16)).ToArray()));
            // Layout markup does not change the spoken spelling. Keep the original literal separately.
            text=Regex.Replace(text,@"(?<=[-/])<br\s*/?>","");
            text=Regex.Replace(text,@"<br\s*/?>"," ");
            text=Regex.Replace(text,@"</?(?:font|sub|sup|b|i|u)(?:\s+[^<>]*)?>","",RegexOptions.CultureInvariant);
            text=Regex.Replace(text.Trim(),@"\s+"," ").Normalize();
        }
        catch(DecoderFallbackException){Diagnostics.Add(id+": InvalidEscapedUtf8Name");return null;}
        if(text.Contains('\\')||text.Contains("@@",StringComparison.Ordinal)||text.Any(char.IsControl)||text.Contains('<')||text.Contains('>'))
        {Diagnostics.Add(id+": UnresolvedNameLiteral "+raw);return null;}
        return text;
    }
    private static string[] Array(Unit unit,string key)
        =>unit.Attributes.TryGetValue(key,out var value)&&value is IEnumerable list&&value is not string
            ?list.Cast<object>().Select(Value).ToArray():[];
    private static string Text(Unit unit,string key)=>unit.Attributes.TryGetValue(key,out var value)?Value(value):"";
    private static string Value(object? value)
        =>value is null?"":Convert.ToString(value is Token token?token.ToString():
            value.GetType().GetProperty("Value")?.GetValue(value)??value,CultureInfo.InvariantCulture)?.Trim('"')??"";
}
