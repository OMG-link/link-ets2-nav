using TruckLib;
using TruckLib.HashFs;

namespace DevHost.IntegrationTests;

public sealed class NavigationArchiveTests
{
    [Fact]
    public void NavigationPreservesArchivePathsContentsAndLayerPriority()
    {
        const string path = "/def/world/road.sii";
        using var baseArchive = Open(Archive(new HashFsV1Writer(), path, [17]), true);
        byte[] overrideData = Enumerable.Repeat((byte)41, 2048).ToArray();
        byte[] archive = Archive(new HashFsV2Writer(), path, overrideData);
        using var normal = Open(archive, false);
        using var navigation = Open(archive, true);
        var expected = Assert.IsType<EntryV2>(normal.GetEntry(path));
        var entry = navigation.GetEntry(path);

        Assert.IsType<EntryV1>(baseArchive.GetEntry(path));
        Assert.IsNotType<EntryV2>(entry);
        Assert.Equal((expected.Hash, expected.Offset, expected.Size, expected.CompressedSize, expected.IsCompressed),
            (entry.Hash, entry.Offset, entry.Size, entry.CompressedSize, entry.IsCompressed));
        Assert.Equal(EntryType.File, navigation.EntryExists(path));
        Assert.Equal(EntryType.Directory, navigation.EntryExists("/def"));
        Assert.Equal(EntryType.NotFound, navigation.EntryExists("/missing"));
        Assert.Equal(["/def/world"], navigation.GetDirectoryListing("/def").Subdirectories);
        Assert.Equal([path], navigation.GetDirectoryListing("/def/world").Files);
        Assert.Equal(normal.Entries.Keys.ToArray(), navigation.Entries.Keys.ToArray());
        Assert.Equal(overrideData, Assert.Single(navigation.Extract(entry, path)));
        using var second = Open(archive, true);
        Assert.Equal(overrideData, Assert.Single(navigation.Extract(second.GetEntry(path), path)));
        Assert.Equal(overrideData, Assert.Single(navigation.Extract(expected, path)));
        Assert.Equal(overrideData, Assert.Single(normal.Extract(entry, path)));
        var changed = expected;
        changed.IsDirectory = true;
        Assert.Throws<FileNotFoundException>(() => navigation.Extract(changed, path));
        Assert.Equal(overrideData, new AssetLoader([navigation, baseArchive]).ReadAllBytes(path));
        Assert.Equal(new byte[] { 17 }, new AssetLoader([baseArchive, navigation]).ReadAllBytes(path));
    }

    [Fact]
    public void PackedTextureStillProvidesBothOriginalFiles()
    {
        using var fixture = typeof(NavigationArchiveTests).Assembly.GetManifestResourceStream("PackedTexture.scs")!;
        using var bytes = new MemoryStream();
        fixture.CopyTo(bytes);
        using var normal = Open(bytes.ToArray(), false);
        using var navigation = Open(bytes.ToArray(), true);
        const string path = "/sample.tobj";

        Assert.NotNull(Assert.IsType<EntryV2>(navigation.GetEntry(path)).TobjMetadata);
        Assert.Equal(normal.GetDirectoryListing("/").Files, navigation.GetDirectoryListing("/").Files);
        var expected = normal.Extract(path);
        Assert.Collection(navigation.Extract(path), tobj => Assert.Equal(expected[0], tobj), dds => Assert.Equal(expected[1], dds));
    }

    private static byte[] Archive(HashFsWriterBase writer, string path, byte[] contents)
    {
        using var data = new MemoryStream();
        writer.Add(contents, path);
        writer.Save(data);
        return data.ToArray();
    }

    private static IHashFsReader Open(byte[] bytes, bool navigationOnly)
    {
        const string mode = "LinkEts2Nav.NavigationOnly";
        AppContext.TryGetSwitch(mode, out bool previous);
        try
        {
            AppContext.SetSwitch(mode, navigationOnly);
            return HashFsReader.Open(new MemoryStream(bytes, writable: false));
        }
        finally { AppContext.SetSwitch(mode, previous); }
    }
}
