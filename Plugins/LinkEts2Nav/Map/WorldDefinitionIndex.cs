using System.Collections.ObjectModel;
using System.Globalization;
using TruckLib.Sii;

namespace LinkEts2Nav.Map;

public sealed record GameResourceDefinition(string Resource,Unit Unit,string Descriptor);

/// <summary>Shared structured projections of the model, sign, prefab and hookup catalogues.</summary>
public sealed class WorldDefinitionIndex
{
    public IReadOnlyDictionary<string,(float MeasurementDistance,bool ReverseDirection)> Cameras {get;}
    public IReadOnlyDictionary<string,GameResourceDefinition> Models {get;}
    public IReadOnlyDictionary<string,GameResourceDefinition> Signs {get;}
    public IReadOnlyDictionary<string,GameResourceDefinition> Prefabs {get;}
    internal WorldDefinitionIndex(GameDataManager data)
    {
        var cameras=new Dictionary<string,(float,bool)>(StringComparer.Ordinal);
        var models=new Dictionary<string,GameResourceDefinition>(StringComparer.Ordinal);
        var signs=new Dictionary<string,GameResourceDefinition>(StringComparer.Ordinal);
        var prefabs=new Dictionary<string,GameResourceDefinition>(StringComparer.Ordinal);
        foreach(string path in data.GetFiles("/unit/hookup/").Where(p=>p.EndsWith(".sii",StringComparison.OrdinalIgnoreCase)))
            data.TryDefinitions(path,document=>
            {
                foreach(var unit in document.OfClass("speed_camera"))
                {
                    unit.Attributes.TryGetValue("measurement_distance",out var distance);
                    unit.Attributes.TryGetValue("reverse_direction",out var reverse);
                    cameras[unit.Name]=CameraValues((object?)distance,(object?)reverse);
                }
            });
        foreach(string path in data.GetFiles("/def/world/").Where(p=>p.EndsWith(".sii",StringComparison.OrdinalIgnoreCase)&&
            new[]{"model.","sign.","prefab."}.Any(prefix=>Path.GetFileName(p).StartsWith(prefix,StringComparison.Ordinal))))
            data.TryDefinitions(path,document=>
            {
                foreach(var unit in document.OfClass("prefab_model"))
                    if(unit.Attributes.ContainsKey("prefab_desc"))prefabs[unit.Name.Split('.').Last()]=new(path,unit,Text(unit,"prefab_desc"));
                foreach(var unit in document.OfClass("model_def"))
                    if(unit.Attributes.ContainsKey("model_desc"))models[unit.Name.Split('.').Last()]=new(path,unit,Text(unit,"model_desc"));
                foreach(var unit in document.OfClass("sign_model"))
                    if(unit.Attributes.ContainsKey("model_desc"))signs[unit.Name.Split('.').Last()]=new(path,unit,Text(unit,"model_desc"));
            });
        Cameras=new ReadOnlyDictionary<string,(float,bool)>(cameras);
        Models=new ReadOnlyDictionary<string,GameResourceDefinition>(models);
        Signs=new ReadOnlyDictionary<string,GameResourceDefinition>(signs);
        Prefabs=new ReadOnlyDictionary<string,GameResourceDefinition>(prefabs);
    }
    public static (float MeasurementDistance,bool ReverseDirection) CameraValues(object? distance,object? reverse)
    {
        float value=Convert.ToSingle(distance,CultureInfo.InvariantCulture);
        bool reversed=Convert.ToBoolean(reverse,CultureInfo.InvariantCulture);
        if(!float.IsFinite(value)||value<0)throw new InvalidDataException("Speed-camera measurement distance must be finite and nonnegative.");
        return(value,reversed);
    }
    internal static string Text(Unit unit,string key)
    {
        if(!unit.Attributes.TryGetValue(key,out var value)||value is null)return "";
        object boxed=value;
        return (Convert.ToString(boxed.GetType().GetProperty("Value")?.GetValue(boxed)??boxed,CultureInfo.InvariantCulture)??"").Trim('"');
    }
}
