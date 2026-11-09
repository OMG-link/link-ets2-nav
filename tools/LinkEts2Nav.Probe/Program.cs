using System.Globalization;
using System.Numerics;
using System.Text.Json;
using LinkEts2Nav.Map;
using TruckLib.Sii;

if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("Usage: LinkEts2Nav.Probe --game-path <ETS2 directory> --x <world X> --z <world Z> [--radius 1000] [--game-log <game.log.txt>] [--archive <name.scs> ...] [--inspect-model <virtual PMG path>]");
    Console.WriteLine("SVG: --game-path <ETS2 directory> --prefab-svg <virtual .ppd path> --output <file.svg> [--game-log <game.log.txt>]");
    Console.WriteLine("Reads local game archives only; does not start ETS2 or ETS2LA. Default mounts installed official archives, not the active profile. Explicit --archive order is highest priority first.");
    return 0;
}

OfficialArchiveMount? mounted = null;
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var archives = new List<string>();
    for (int i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}");
        if (args[i] == "--archive") archives.Add(args[i + 1]);
        else if (args[i] is "--game-path" or "--x" or "--z" or "--radius" or "--game-log" or "--inspect-model" or "--prefab-svg" or "--output")
        {
            if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Repeated option {args[i]}");
        }
        else throw new ArgumentException($"Unknown option {args[i]}");
    }
    string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}");
    float Number(string text) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
        && float.IsFinite(value) ? value : throw new ArgumentException($"Invalid coordinate/distance: {text}");
    string root = Path.GetFullPath(Required("--game-path"));
    bool svgMode = options.TryGetValue("--prefab-svg", out string? prefabSvg);
    bool inspectModel = options.TryGetValue("--inspect-model", out string? modelPath);
    float x = Number(inspectModel || svgMode ? options.GetValueOrDefault("--x", "0") : Required("--x"));
    float z = Number(inspectModel || svgMode ? options.GetValueOrDefault("--z", "0") : Required("--z"));
    float radius = Number(options.GetValueOrDefault("--radius", "1000"));
    if (radius <= 0 || radius > 4000) throw new ArgumentException("Radius must be greater than 0 and at most 4000 metres.");
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
    if (options.TryGetValue("--game-log", out string? logPath))
    {
        if (archives.Count > 0) throw new ArgumentException("--game-log and --archive are mutually exclusive.");
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        mounted = OfficialArchiveMount.FromGameLog(root, reader.ReadToEnd().Split('\n'));
    }
    else
    {
        if (archives.Count == 0) archives.AddRange(OfficialArchiveMount.FindInstalledArchives(root));
        else for (int i = 0; i < archives.Count; i++) archives[i] = Path.GetFullPath(archives[i], root);
        mounted = new OfficialArchiveMount(archives);
    }
    var fs = mounted.FileSystem;
    if (svgMode)
    {
        if(inspectModel)throw new ArgumentException("--prefab-svg and --inspect-model are mutually exclusive.");
        string svgOutput=Path.GetFullPath(Required("--output"));
        var descriptor=TruckLib.Models.Ppd.PrefabDescriptor.Open(prefabSvg!,fs);
        Directory.CreateDirectory(Path.GetDirectoryName(svgOutput)!);
        PrefabSvgExporter.Write(descriptor,prefabSvg!,svgOutput);
        Console.WriteLine($"SVG saved: {svgOutput} ({descriptor.Nodes.Count} control nodes, {descriptor.NavCurves.Count} curves)");
        return 0;
    }
    if (inspectModel)
    {
        var definitions = new Dictionary<string, (float MeasurementDistance, bool ReverseDirection)>();
        foreach (var path in fs.GetFiles("/unit/hookup/").Where(p => p.EndsWith(".sii", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var unit in SiiFile.Open(path, fs).Units)
            {
                if (unit.Class != "speed_camera") continue;
                float distance = unit.Attributes.TryGetValue("measurement_distance", out var value)
                    ? Convert.ToSingle((object)value, CultureInfo.InvariantCulture) : 0;
                bool reverse = unit.Attributes.TryGetValue("reverse_direction", out var reversed) && Convert.ToBoolean((object)reversed, CultureInfo.InvariantCulture);
                definitions[unit.Name] = (distance, reverse);
            }
        }
        using var stream = fs.Open(modelPath!);
        var locators = PmgLocatorReader.Read(stream, definitions);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            modelPath, profileResolved = mounted.ProfileResolved,
            locators = locators.Select(l => new
            {
                l.Name, l.Hookup, l.MeasurementDistance, l.ReverseDirection, l.PartIndex, l.Scale,
                position = new { l.Position.X, l.Position.Y, l.Position.Z },
                rotation = new { l.Rotation.X, l.Rotation.Y, l.Rotation.Z, l.Rotation.W }
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    var scanner = new NearbyCameraScanner(fs);
    // TruckLib emits sector diagnostics to stdout. Keep machine-readable results clean.
    var output = Console.Out;
    NearbyMapSnapshot snapshot;
    try { Console.SetOut(Console.Error); snapshot = scanner.ReadNearby(new Vector3(x, 0, z), radius); }
    finally { Console.SetOut(output); }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        profileResolved = mounted.ProfileResolved,
        archivePriority = mounted.ArchivePaths.Select(Path.GetFileName),
        position = new { x, z }, radiusMeters = radius,
        sectors = snapshot.Sectors.Select(s => new { s.X, s.Z }),
        items = snapshot.Map.MapItems.Count,
        cameras = snapshot.Cameras.Select(c => new
        {
            c.Id, c.Hookup, c.SourceDescriptor, c.DirectionVerified, c.MeasurementDistanceMeters, c.ReverseDirection,
            c.LocatorName, c.ModelVariant, c.MetadataFingerprint, c.ActivePartVerified,
            locatorRotation = new { c.LocatorRotation.X, c.LocatorRotation.Y, c.LocatorRotation.Z, c.LocatorRotation.W },
            locator = new { c.LocatorPosition.X, c.LocatorPosition.Y, c.LocatorPosition.Z },
            monitoredPosition = new { c.MonitoredPosition.X, c.MonitoredPosition.Y, c.MonitoredPosition.Z },
            travelDirection = new { c.TravelDirection.X, c.TravelDirection.Y, c.TravelDirection.Z }
        }),
        diagnostics = snapshot.Diagnostics
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Probe failed: {ex.Message}");
    return 1;
}
finally
{
    mounted?.Dispose();
}
