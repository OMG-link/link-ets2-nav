using System.IO.Compression;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib.HashFs;

namespace LinkEts2Nav.UnitTests;

public sealed class ArchiveDispatchTests
{
    private sealed class TempArchive:IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"LinkNav-archive-"+Guid.NewGuid().ToString("N")+".scs");
        public void Dispose()=>File.Delete(Path);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZipWithOrWithoutLeadingStubUsesItsOwnFormat(bool stub)
    {
        using var file=new TempArchive();
        using(var stream=File.Create(file.Path))
        {
            if(stub)stream.Write(Encoding.ASCII.GetBytes("test-leading-stub"));
            using var zip=new ZipArchive(stream,ZipArchiveMode.Create);
            zip.CreateEntry("model/camera.pmg");
        }
        Assert.Equal("model/camera.pmg",OfficialArchiveMount.FindRelevantOverride(file.Path));
    }
    [Fact]
    public void EmptyZipIsAValidArchiveWithNoRelevantOverrides()
    {
        using var file=new TempArchive();using(ZipFile.Open(file.Path,ZipArchiveMode.Create)){}
        Assert.Null(OfficialArchiveMount.FindRelevantOverride(file.Path));
    }
    [Theory]
    [InlineData(1,false)]
    [InlineData(1,true)]
    [InlineData(2,false)]
    [InlineData(2,true)]
    public void BothHashFsVersionsDispatchBySignature(int version,bool relevant)
    {
        using var file=new TempArchive();
        HashFsWriterBase writer=version==1?new HashFsV1Writer():new HashFsV2Writer();
        writer.CompressionLevel=CompressionLevel.NoCompression;
        string asset=relevant?"/model/camera.pmg":"/def/world/traffic_rules.sii";
        using var content=new MemoryStream(Encoding.UTF8.GetBytes("test"));writer.Add(content,asset);writer.Save(file.Path);
        Assert.Equal(relevant?asset:null,OfficialArchiveMount.FindRelevantOverride(file.Path));
    }
    [Fact]
    public void DamagedHashFsKeepsReaderExceptionAndReleasesOpenedStream()
    {
        using var file=new TempArchive();File.WriteAllBytes(file.Path,"SCS#"u8.ToArray());
        Assert.Throws<EndOfStreamException>(()=>OfficialArchiveMount.FindRelevantOverride(file.Path));
        using var exclusive=File.Open(file.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    [Fact]
    public void DamagedZipDoesNotRetryAsHashFs()
    {
        using var file=new TempArchive();File.WriteAllBytes(file.Path,[0x50,0x4B,0x03,0x04]);
        var error=Assert.Throws<InvalidDataException>(()=>OfficialArchiveMount.FindRelevantOverride(file.Path));
        Assert.Contains("Zip",error.StackTrace,StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void PartialOfficialMountFailureReleasesBothEarlierReaderAndFailingStream()
    {
        using var good=new TempArchive();using var bad=new TempArchive();
        new HashFsV1Writer().Save(good.Path);File.WriteAllBytes(bad.Path,"SCS#"u8.ToArray());
        Assert.Throws<EndOfStreamException>(()=>new OfficialArchiveMount([good.Path,bad.Path]));
        using var first=File.Open(good.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var second=File.Open(bad.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    [Fact]
    public void MalformedTollgateDefinitionPropagatesParserFailure()
    {
        using var file=new TempArchive();const string content="SiiNunit";
        using(var zip=ZipFile.Open(file.Path,ZipArchiveMode.Create))
        using(var writer=new StreamWriter(zip.CreateEntry("unit/hookup/tollgate.sii").Open()))writer.Write(content);
        var original=Record.Exception(()=>TruckLib.Sii.SiiFile.Load(Encoding.UTF8.GetBytes(content),"/unit/hookup/tollgate.sii"));Assert.NotNull(original);
        var observed=Record.Exception(()=>OfficialArchiveMount.FindRelevantOverride(file.Path));
        Assert.NotNull(observed);Assert.Equal(original.GetType(),observed.GetType());
        Assert.Contains("Sii",observed.StackTrace,StringComparison.OrdinalIgnoreCase);
    }
}
