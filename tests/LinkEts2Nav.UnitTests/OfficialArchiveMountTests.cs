using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class OfficialArchiveMountTests
{
    [Fact]
    public void ResolverUsesMountedOfficialArchivesAndLastMountPriority()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LinkNavGame"));
        string basePath = Path.Combine(root, "base.scs");
        string dlcPath = Path.Combine(root, "dlc_east.scs");
        var plan = OfficialArchiveMount.ResolveGameLog(root,
        [
            "[hashfs] dlc_balt.scs: Created and validated, 42 entries",
            $"[fs] device {basePath} mounted to scs pool.",
            $"[fs] device {dlcPath} mounted to scs pool.",
            $"[fs] device {Path.Combine(root, "unofficial.scs")} mounted to scs pool."
        ]);
        Assert.Equal(new[] { dlcPath, basePath }, plan.ArchivePaths);
        Assert.False(plan.HasMods);
    }

    [Fact]
    public void ResolverRemovesUnmountedArchivesAndTracksMods()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LinkNavGame"));
        string mod = Path.Combine(Path.GetTempPath(), "economy.zip");
        var plan = OfficialArchiveMount.ResolveGameLog(root,
        [
            $"[fs] device {Path.Combine(root, "base.scs")} mounted to scs pool.",
            $"[fs] device {mod} mounted to mod pool.",
            "[hashfs] base.scs: Unmounted."
        ]);
        Assert.Empty(plan.ArchivePaths);
        Assert.True(plan.HasMods);
        Assert.Equal(mod, Assert.Single(plan.ModPaths));
    }

    [Fact]
    public void WinterDlcIsIncludedWhenMountedAndUnmountedModsAreRemoved()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LinkNavGame"));
        string winter = Path.Combine(root, "dlc_winter.scs");
        string mod = Path.Combine(Path.GetTempPath(), "old_mod.zip");
        var plan = OfficialArchiveMount.ResolveGameLog(root,
        [
            $"[fs] device {winter} mounted to scs pool.",
            $"[fs] device {mod} mounted to mod pool.",
            $"[zipfs] {mod}: Unmounted."
        ]);
        Assert.Equal(winter, Assert.Single(plan.ArchivePaths));
        Assert.False(plan.HasMods);
    }

    [Theory]
    [InlineData("map/europe/sec+0000+0000.base", true)]
    [InlineData("model/speedcamera/camera.pmg", true)]
    [InlineData("prefab/road.ppd", true)]
    [InlineData("unit/hookup/speed_camera.sii", true)]
    [InlineData("def/world/model.sii", true)]
    [InlineData("def/economy_data.sii", false)]
    [InlineData("def/traffic_data.sii", false)]
    [InlineData("manifest.sii", false)]
    public void OnlyMapRelevantOverridesBlockOfficialMapMode(string path, bool expected)
        => Assert.Equal(expected, OfficialArchiveMount.IsRelevantAsset(path));
}
