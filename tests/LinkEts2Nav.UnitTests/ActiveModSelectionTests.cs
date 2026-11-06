using System.IO.Compression;
using LinkEts2Nav.Map;

namespace LinkEts2Nav.UnitTests;

public sealed class ActiveModSelectionTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "LinkNavProfile");
    private static string Workshop(string id, string package = "latest")
        => Path.Combine(Root, "workshop", "content", "227300", id, package);

    [Fact]
    public void ProfileSelectionExcludesModManagerPreviewMounts()
    {
        string traffic = Workshop("123");
        string economy = Workshop("456", "universal.zip");
        string hotfix = Path.Combine(Root, "mod", "economy_hotfix.scs");
        var plan = OfficialArchiveMount.ResolveGameLog(Root,
        [
            $"[fs] device {Workshop("999", "radar")} mounted to mod pool.",
            "[mods] Active 3 mods (local: 1, workshop: 2)",
            "[mods] Active workshop mod ID 123 (name: Traffic, version: 1, author: test)",
            "[mods] Active workshop mod ID 456 (name: Economy, version: 1, author: test)",
            "[mods] Active local mod economy_hotfix (name: Hotfix, version: 1, author: test)",
            $"[fs] device {traffic} mounted to mod pool.",
            $"[fs] device {economy} mounted to mod pool.",
            $"[fs] device {hotfix} mounted to mod pool.",
            "[zipfs] universal.zip: Unmounted",
            "[zipfs] economy_hotfix.scs: Unmounted",
            $"[fs] device {Workshop("999", "radar")} mounted to mod pool.",
            $"[fs] device {Workshop("777", "universal.zip")} mounted to mod pool."
        ]);
        Assert.Equal(new[] { traffic, economy, hotfix }, plan.ModPaths);
        Assert.Equal(3, plan.ExpectedModCount);
    }

    [Fact]
    public void LatestExplicitActiveListReplacesPreviousProfile()
    {
        string current = Workshop("123");
        var plan = OfficialArchiveMount.ResolveGameLog(Root,
        [
            "[mods] Active 1 mods (local: 0, workshop: 1)",
            "[mods] Active workshop mod ID 999 (name: Radar, version: 1, author: test)",
            $"[fs] device {Workshop("999", "radar")} mounted to mod pool.",
            "[mods] Active 1 mods (local: 0, workshop: 1)",
            "[mods] Active workshop mod ID 123 (name: Traffic, version: 1, author: test)",
            $"[fs] device {current} mounted to mod pool."
        ]);
        Assert.Equal(current, Assert.Single(plan.ModPaths));
    }

    [Fact]
    public void ExplicitZeroModsIgnoresPreviewDevices()
    {
        var plan = OfficialArchiveMount.ResolveGameLog(Root,
        [
            "[mods] Active 0 mods (local: 0, workshop: 0)",
            $"[fs] device {Workshop("999", "radar")} mounted to mod pool."
        ]);
        Assert.False(plan.HasMods);
        Assert.Equal(0, plan.ExpectedModCount);
    }

    [Fact]
    public void MissingProfileMountNeverSilentlyIgnoresAnActiveMod()
    {
        Assert.Throws<InvalidDataException>(() => OfficialArchiveMount.FromGameLog(Root,
        [
            "[mods] Active 1 mods (local: 0, workshop: 1)",
            "[mods] Active workshop mod ID 123 (name: Map, version: 1, author: test)",
            $"[fs] device {Workshop("999")} mounted to mod pool."
        ]));
    }

    [Fact]
    public void SameBasenameDoesNotRemoveAnotherWorkshopPackage()
    {
        string first = Workshop("111", "universal.zip"), second = Workshop("222", "universal.zip");
        var plan = OfficialArchiveMount.ResolveGameLog(Root,
        [
            $"[fs] device {first} mounted to mod pool.",
            $"[fs] device {second} mounted to mod pool.",
            $"[zipfs] {first}: Unmounted"
        ]);
        Assert.Equal(second, Assert.Single(plan.ModPaths));
    }

    [Theory]
    [InlineData("def/world/traffic_lane.sii")]
    [InlineData("def/world/traffic_lane.dlc_xmas_2023_event.sii")]
    [InlineData("def/world/traffic_rules_spawn.cip.sui")]
    [InlineData("def/world/traffic_rules.sii")]
    [InlineData("def/world/semaphore_profile.sii")]
    [InlineData("DEF\\WORLD\\TRAFFIC_RULES.DLC_XMAS_2023_EVENT.SII")]
    [InlineData("def/world/")]
    public void AiBehaviorDefinitionsAreNotGeometryOverrides(string path)
        => Assert.False(OfficialArchiveMount.IsRelevantAsset(path));

    [Theory]
    [InlineData("def/world/model.cip.sii")]
    [InlineData("def/world/sign.sii")]
    [InlineData("def/world/road_look.sii")]
    [InlineData("def/world/nested/model.sii")]
    [InlineData("unit/hookup/speed_camera.sii")]
    public void ModelAndRoadDefinitionsRemainProtected(string path)
        => Assert.True(OfficialArchiveMount.IsRelevantAsset(path));

    [Fact]
    public void ZipDirectoriesAndTrafficDefinitionsDoNotBlockButModelFileDoes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"LinkNav-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                zip.CreateEntry("def/world/");
                zip.CreateEntry("def/world/traffic_rules.sii");
            }
            Assert.False(OfficialArchiveMount.HasRelevantOverrides(path));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                zip.CreateEntry("model/speedcamera/camera.pmg");
            Assert.Equal("model/speedcamera/camera.pmg", OfficialArchiveMount.FindRelevantOverride(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("tollgate", "tollgate.end", false)]
    [InlineData("lod_model_hookup", "toll.cabin", false)]
    [InlineData("speed_camera", "speedcam.toll", true)]
    [InlineData("lod_model_hookup", "speedcam.20", true)]
    public void TollgateExceptionRequiresNonCameraUnitTypes(string kind, string name, bool blocked)
    {
        string path = Path.Combine(Path.GetTempPath(), $"LinkNav-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("unit/hookup/tollgate.sii").Open()))
                writer.Write($"SiiNunit\n{{\n{kind} : {name}\n{{\ntoll: 0.2\n}}\n}}");
            Assert.Equal(blocked, OfficialArchiveMount.HasRelevantOverrides(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TollgateIncludesCannotHideUnverifiedCameraDefinitions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"LinkNav-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("unit/hookup/tollgate.sii").Open()))
                writer.Write("SiiNunit\n{\n@include \"extra.sui\"\n}");
            Assert.True(OfficialArchiveMount.HasRelevantOverrides(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void PreviewMountsDoNotInvalidateTheMapCache()
    {
        string[] loaded = [
            "[mods] Active 1 mods (local: 0, workshop: 1)",
            "[mods] Active workshop mod ID 123 (name: Traffic, version: 1, author: test)",
            $"[fs] device {Workshop("123")} mounted to mod pool."
        ];
        var before = OfficialArchiveMount.ResolveGameLog(Root, loaded);
        var after = OfficialArchiveMount.ResolveGameLog(Root, loaded.Concat([
            $"[fs] device {Workshop("999", "radar")} mounted to mod pool.",
            "[zipfs] universal.zip: Unmounted"
        ]));
        Assert.Equal(before.Signature, after.Signature);
        // Reloading the profile is a real boundary even with the same package paths.
        Assert.NotEqual(before.Signature, OfficialArchiveMount.ResolveGameLog(Root, loaded.Concat(loaded)).Signature);
    }
}
