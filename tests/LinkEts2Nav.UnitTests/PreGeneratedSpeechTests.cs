using System.Text.Json;
using LinkEts2Nav.Runtime;

namespace LinkEts2Nav.UnitTests;

public sealed class PreGeneratedSpeechTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"LinkNav-preload-"+Guid.NewGuid().ToString("N"));
    private string Manifest=>Path.Combine(directory,"manifest.json");
    [Fact]
    public void MissingOptionalDirectoryOrManifestIsStillAnEmptyPack()
    {
        Assert.Empty(PreGeneratedSpeech.Load(directory));
        Directory.CreateDirectory(directory);Assert.Empty(PreGeneratedSpeech.Load(directory));
    }
    [Fact]
    public void MalformedExistingManifestDoesNotSelectSynthesisFallback()
    {
        Directory.CreateDirectory(directory);File.WriteAllText(Manifest,"{");
        var messages=new List<string>();
        Assert.ThrowsAny<JsonException>(()=>PreGeneratedSpeech.Load(directory,messages.Add));
        Assert.Empty(messages);
    }
    [Fact]
    public void InvalidManifestFieldsRetainTheirActualError()
    {
        Directory.CreateDirectory(directory);File.WriteAllText(Manifest,"{}");
        Assert.Throws<KeyNotFoundException>(()=>PreGeneratedSpeech.Load(directory));
    }
    [Fact]
    public void DirectoryNamedManifestIsInvalidRatherThanAnAbsentOptionalPack()
    {
        Directory.CreateDirectory(Manifest);
        Assert.Throws<InvalidDataException>(()=>PreGeneratedSpeech.Load(directory));
        Directory.Delete(Manifest);
    }
    [Fact]
    public void InvalidPathIsNotDisguisedAsAnUninstalledPack()
        =>Assert.Throws<ArgumentException>(()=>PreGeneratedSpeech.Load("\0"));
    [Fact]
    public void MissingReferencedAudioIsAnExplicitFileError()
    {
        Directory.CreateDirectory(directory);
        const string text="左转。";string file=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))+".wav";
        File.WriteAllText(Manifest,JsonSerializer.Serialize(new{Schema=1,Entries=new[]{new{Text=text,File=file,Sha256="missing"}}}));
        Assert.Throws<FileNotFoundException>(()=>PreGeneratedSpeech.Load(directory));
    }
    public void Dispose()
    {
        if(File.Exists(Manifest))File.Delete(Manifest);
        if(Directory.Exists(Manifest))Directory.Delete(Manifest);
        if(Directory.Exists(directory))Directory.Delete(directory);
    }
}
