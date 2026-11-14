using System.Globalization;
using System.Text;
using TruckLib;
using TruckLib.Sii;

namespace LinkEts2Nav.Probe;

internal static class CityNameExporter
{
    public static void Export(IFileSystem fs, string output, string? country)
    {
        static string Text(Unit unit, string key)
        {
            if (!unit.Attributes.TryGetValue(key, out var value) || value is null) return "";
            if (value is Token token) return token.ToString();
            var inner = value.GetType().GetProperty("Value");
            return Convert.ToString(inner is null ? value : inner.GetValue(value), CultureInfo.InvariantCulture) ?? "";
        }
        var cities = new Dictionary<string, Unit>(StringComparer.Ordinal);
        foreach (string file in fs.GetFiles("/def/").Where(p => fs.GetParent(p).TrimEnd('/') == "/def"
            && Path.GetFileName(p).StartsWith("city", StringComparison.Ordinal) && p.EndsWith(".sii", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            foreach (var unit in SiiFile.Open(file, fs).Units.Where(u => u.Class == "city_data"))
                cities[unit.Name] = unit;
        string[] names = cities.Values.Where(u => country is null
                || Text(u, "country").Split('.').Last().Equals(country, StringComparison.OrdinalIgnoreCase))
            .Select(u => Text(u, "city_name").Trim().Normalize(NormalizationForm.FormC))
            .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new InvalidDataException("No matching city names in the selected game archives.");
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, names, new UTF8Encoding(false));
        Console.WriteLine("Exported " + names.Length + " city definition names. Sign aliases and map reachability are not inferred.");
    }
}
