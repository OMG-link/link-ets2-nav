using System.Text.RegularExpressions;
using System.IO.Compression;
using TruckLib;
using TruckLib.HashFs;

namespace LinkEts2Nav.Map;

public sealed record OfficialArchivePlan(IReadOnlyList<string> ArchivePaths, bool HasMods, IReadOnlyList<string> ModPaths);

/// <summary>Owns independent archive readers; never shares mutable host filesystem streams.</summary>
public sealed class OfficialArchiveMount : IDisposable
{
    private readonly List<IFileSystem> readers = [];
    public IFileSystem FileSystem { get; }
    public IReadOnlyList<string> ArchivePaths { get; }
    public bool ProfileResolved { get; private set; }

    public OfficialArchiveMount(IEnumerable<string> paths)
    {
        var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0) throw new ArgumentException("No official game archives were selected.", nameof(paths));
        try
        {
            foreach (var path in selected) readers.Add((IFileSystem)HashFsReader.Open(path));
            FileSystem = new AssetLoader(readers.ToArray());
            ArchivePaths = Array.AsReadOnly(selected);
        }
        catch { Dispose(); throw; }
    }

    public static OfficialArchiveMount FromGameLog(string gamePath, IEnumerable<string> logLines)
    {
        var plan = ResolveGameLog(gamePath, logLines);
        foreach (string mod in plan.ModPaths)
        {
            if (HasRelevantOverrides(mod))
                throw new NotSupportedException($"Active mod overrides map, road or camera assets: {Path.GetFileName(mod)}");
        }
        return new(plan.ArchivePaths) { ProfileResolved = true };
    }

    /// <summary>Later mounts take precedence; validated-but-unmounted DLCs are excluded.</summary>
    public static OfficialArchivePlan ResolveGameLog(string gamePath, IEnumerable<string> logLines)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        var active = new List<string>();
        var mods = new List<string>();
        foreach (string line in logLines)
        {
            var mounted = Regex.Match(line, @"\[fs\] device (.+?) mounted to (scs|core|mod) pool\.");
            if (mounted.Success)
            {
                string pool = mounted.Groups[2].Value;
                string path = Path.GetFullPath(mounted.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                if (pool == "mod")
                {
                    if (!mods.Contains(path, StringComparer.OrdinalIgnoreCase)) mods.Add(path);
                    continue;
                }
                if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
                    || !IsOfficialArchive(Path.GetFileName(path))) continue;
                active.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                active.Add(path);
                continue;
            }
            var unmounted = Regex.Match(line, @"\[(?:hashfs|zipfs)\] (.+?): Unmounted\.");
            if (unmounted.Success)
            {
                string name = Path.GetFileName(unmounted.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                active.RemoveAll(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
                mods.RemoveAll(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
            }
        }
        active.Reverse();
        return new(active.AsReadOnly(), mods.Count > 0, mods.AsReadOnly());
    }

    /// <summary>Pure economy/traffic definitions are allowed; unknown or protected archives fail closed.</summary>
    public static bool HasRelevantOverrides(string modPath)
    {
        if (Directory.Exists(modPath))
        {
            return Directory.EnumerateFiles(modPath, "*", SearchOption.AllDirectories)
                .Any(path => IsRelevantAsset(Path.GetRelativePath(modPath, path)));
        }
        try
        {
            using var zip = ZipFile.OpenRead(modPath);
            return zip.Entries.Any(entry => IsRelevantAsset(entry.FullName));
        }
        catch (InvalidDataException)
        {
            try
            {
                var fs = (IFileSystem)HashFsReader.Open(modPath);
                try
                {
                    return new[] { "/map", "/model", "/model2", "/prefab", "/prefab2", "/road_template", "/unit/hookup", "/def/world" }
                        .Any(path => fs.DirectoryExists(path));
                }
                finally { (fs as IDisposable)?.Dispose(); }
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Cannot verify active mod archive {Path.GetFileName(modPath)}: {ex.Message}", ex);
            }
        }
    }

    public static bool IsRelevantAsset(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        return new[] { "map/", "model/", "model2/", "prefab/", "prefab2/", "road_template/", "unit/hookup/", "def/world/" }
            .Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> FindInstalledArchives(string gamePath)
        => Directory.GetFiles(gamePath, "*.scs").Where(p => IsOfficialArchive(Path.GetFileName(p)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();

    public static bool IsOfficialArchive(string name)
        => name.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)
                || name is "base.scs" or "base_cfg.scs" or "base_map.scs" or "base_share.scs"
                    or "base_vehicle.scs" or "core.scs" or "def.scs" or "effect.scs" or "locale.scs";

    public void Dispose()
    {
        foreach (var reader in readers.OfType<IDisposable>()) reader.Dispose();
        readers.Clear();
    }
}
