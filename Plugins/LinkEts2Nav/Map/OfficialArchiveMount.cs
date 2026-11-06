using System.Text.RegularExpressions;
using System.IO.Compression;
using TruckLib;
using TruckLib.HashFs;

namespace LinkEts2Nav.Map;

public sealed record OfficialArchivePlan(IReadOnlyList<string> ArchivePaths, bool HasMods, IReadOnlyList<string> ModPaths)
{
    public int? ExpectedModCount { get; init; }
    public string ProfileMarker { get; init; } = "";
    public string Signature => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", ArchivePaths.Concat(ModPaths)
            .Append(ProfileMarker).Append(ExpectedModCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")))));
}

/// <summary>Owns independent archive readers; never shares mutable host filesystem streams.</summary>
public sealed class OfficialArchiveMount : IDisposable
{
    private static readonly string[] RelevantRoots =
        ["map", "model", "model2", "prefab", "prefab2", "road_template", "unit/hookup", "def/world"];
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
        if (plan.ExpectedModCount is { } count && plan.ModPaths.Count != count)
            throw new InvalidDataException($"Active mod list is incomplete: expected {count}, resolved {plan.ModPaths.Count}. Wait for the profile to finish loading.");
        foreach (string mod in plan.ModPaths)
        {
            string? conflict = FindRelevantOverride(mod);
            if (conflict is not null)
                throw new NotSupportedException($"Active mod overrides map, road or camera assets: {mod}; conflicting resource: {conflict}");
        }
        return new(plan.ArchivePaths) { ProfileResolved = true };
    }

    /// <summary>
    /// The latest explicit active-mod block identifies the profile. Subsequent mod-manager
    /// preview mounts must not replace it, even when different workshop files share a basename.
    /// </summary>
    public static OfficialArchivePlan ResolveGameLog(string gamePath, IEnumerable<string> logLines)
    {
        var lines = logLines.ToArray();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        int header = -1;
        int? expected = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"\[mods\] Active (\d+) mods\b");
            if (match.Success) { header = i; expected = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); }
        }
        var workshop = new HashSet<string>(StringComparer.Ordinal);
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (header >= 0)
        {
            for (int i = header + 1; i < lines.Length; i++)
            {
                if (lines[i].Contains("[fs] device ", StringComparison.Ordinal)) break;
                var workshopMatch = Regex.Match(lines[i], @"\[mods\] Active workshop mod ID (\d+)\b");
                var localMatch = Regex.Match(lines[i], @"\[mods\] Active local mod (.+?) \(name:");
                if (workshopMatch.Success) workshop.Add(workshopMatch.Groups[1].Value);
                if (localMatch.Success) local.Add(localMatch.Groups[1].Value);
            }
        }
        bool Declared(string path)
        {
            string normalized = path.Replace('\\', '/');
            var id = Regex.Match(normalized, @"/workshop/content/\d+/(\d+)/", RegexOptions.IgnoreCase);
            return id.Success ? workshop.Contains(id.Groups[1].Value)
                : local.Contains(Path.GetFileName(path)) || local.Contains(Path.GetFileNameWithoutExtension(path));
        }
        var active = new List<string>();
        var mods = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var mounted = Regex.Match(lines[i], @"\[fs\] device (.+?) mounted to (scs|core|mod) pool\.");
            if (mounted.Success)
            {
                string pool = mounted.Groups[2].Value;
                string path = Path.GetFullPath(mounted.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                if (pool == "mod")
                {
                    bool belongs = header < 0 || i > header && mods.Count < expected && Declared(path);
                    if (belongs && !mods.Contains(path, StringComparer.OrdinalIgnoreCase)) mods.Add(path);
                    continue;
                }
                if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)
                    || !IsOfficialArchive(Path.GetFileName(path))) continue;
                active.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                active.Add(path);
                continue;
            }
            var unmounted = Regex.Match(lines[i], @"\[(?:hashfs|zipfs)\] (.+?): Unmounted\.?\s*$");
            if (unmounted.Success)
            {
                string path = unmounted.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                RemoveUnmounted(active, path);
                // With an explicit profile snapshot, later unmounts may be temporary
                // preview readers, not the active game. A new Active block replaces it.
                if (header < 0) RemoveUnmounted(mods, path);
            }
        }
        active.Reverse();
        return new(active.AsReadOnly(), mods.Count > 0, mods.AsReadOnly()) { ExpectedModCount = expected, ProfileMarker = header < 0 ? "" : $"{header}:{lines[header]}" };
    }

    private static void RemoveUnmounted(List<string> paths, string unmounted)
    {
        if (Path.IsPathRooted(unmounted))
            paths.RemoveAll(p => string.Equals(p, Path.GetFullPath(unmounted), StringComparison.OrdinalIgnoreCase));
        else
        {
            var matches = paths.Where(p => string.Equals(Path.GetFileName(p), unmounted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) paths.Remove(matches[0]);
        }
    }

    public static bool HasRelevantOverrides(string modPath) => FindRelevantOverride(modPath) is not null;

    /// <summary>Returns an actual conflicting file, not just a broad directory classification.</summary>
    public static string? FindRelevantOverride(string modPath)
    {
        if (Directory.Exists(modPath))
            return Directory.EnumerateFiles(modPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(modPath, path).Replace('\\', '/')).FirstOrDefault(path => Conflicts(path, () => File.ReadAllBytes(Path.Combine(modPath, path))));
        try
        {
            using var zip = ZipFile.OpenRead(modPath);
            return zip.Entries.FirstOrDefault(entry => entry.Name.Length > 0
                && Conflicts(entry.FullName, () => ReadZipEntry(entry)))?.FullName;
        }
        catch (InvalidDataException)
        {
            try
            {
                var reader = HashFsReader.Open(modPath);
                try
                {
                    var fs = (IFileSystem)reader;
                    var pending = new Stack<string>(RelevantRoots.Select(p => "/" + p).Where(fs.DirectoryExists));
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (pending.TryPop(out string? path))
                    {
                        if (!visited.Add(path)) continue;
                        var listing = reader.GetDirectoryListing(path, filesOnly: false, returnAbsolute: true);
                        string? conflict = listing.Files.FirstOrDefault(path => Conflicts(path, () => fs.ReadAllBytes(path)));
                        if (conflict is not null) return conflict;
                        foreach (string directory in listing.Subdirectories) pending.Push(directory);
                    }
                    return null;
                }
                finally { (reader as IDisposable)?.Dispose(); }
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Cannot verify active mod archive {Path.GetFileName(modPath)}: {ex.Message}", ex);
            }
        }
    }


    private static bool Conflicts(string path, Func<byte[]> read)
    {
        if (!IsRelevantAsset(path)) return false;
        if (!path.Replace('\\', '/').TrimStart('/').Equals("unit/hookup/tollgate.sii", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            byte[] bytes = read();
            if (bytes.Length > 2 * 1024 * 1024) return true;
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            if (!text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("SiiNunit", StringComparison.Ordinal)
                || text.Contains("@include", StringComparison.OrdinalIgnoreCase)) return true;
            var units = TruckLib.Sii.SiiFile.Load(bytes, "/unit/hookup/tollgate.sii").Units;
            return units.Count == 0 || units.Any(unit =>
                unit.Class is not "tollgate" and not "lod_model_hookup"
                || unit.Name.StartsWith("speedcam", StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; } // Unknown definitions retain the original conservative behavior.
    }

    private static byte[] ReadZipEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Hookup definition exceeds supported size.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static bool IsRelevantAsset(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        if (path.EndsWith('/')) return false; // ZIP directory records contain no overrides.
        if (path.StartsWith("def/world/", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(path["def/world/".Length..],
                @"^(traffic_lane|traffic_rules|traffic_rules_spawn|semaphore_profile)(\.[^/]+)?\.(sii|sui)$",
                RegexOptions.IgnoreCase))
            return false; // AI spawn/priority/signal timing does not change road splines or camera locators.
        return RelevantRoots.Any(prefix => path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
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
