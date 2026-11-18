using System.IO.Compression;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib;

namespace LinkEts2Nav.UnitTests;

public sealed class DefinitionModArchiveTests
{
    [Fact]
    public void ZipDefinitionOverlayUsesMountOrderAndRetainsOriginalFiles()
    {
        string a=Path.GetTempFileName(),b=Path.GetTempFileName();
        try
        {
            WriteZip(a,"def/test.sii","first");WriteZip(b,"def/test.sii","second");
            var overlay=GameDefinitionFileSystem.Create(new Files(),[a,b]);
            Assert.Equal("second",overlay.ReadAllText("/def/test.sii"));
            Assert.Equal("official",overlay.ReadAllText("/def/original.sii"));
        }
        finally {File.Delete(a);File.Delete(b);}
    }
    [Fact]
    public void DamagedZipPreservesItsReaderFailureAndReleasesItsFile()
    {
        string file=Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file,[0x50,0x4B,0x03,0x04]);
            var error=Assert.Throws<InvalidDataException>(()=>GameDefinitionFileSystem.Create(new Files(),[file]));
            Assert.Contains("Zip",error.StackTrace,StringComparison.OrdinalIgnoreCase);
            using var exclusive=File.Open(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        }
        finally {File.Delete(file);}
    }
    [Fact]
    public void DefinitionSizeFailureIsNotMislabelledAsUnsupportedArchive()
    {
        string file=Path.GetTempFileName();
        try
        {
            WriteZip(file,"def/test.sii",new string('x',4*1024*1024+1));
            var error=Assert.Throws<InvalidDataException>(()=>GameDefinitionFileSystem.Create(new Files(),[file]));
            Assert.Equal("Mod definition exceeds 4 MiB.",error.Message);
            Assert.Contains("Add",error.StackTrace);
        }
        finally {File.Delete(file);}
    }
    [Fact]
    public void MissingArchiveIsAnIoFailureWithoutTranslation()
    {
        string file=Path.Combine(Path.GetTempPath(),"LinkNav-missing-"+Guid.NewGuid().ToString("N")+".zip");
        Assert.Throws<FileNotFoundException>(()=>GameDefinitionFileSystem.Create(new Files(),[file]));
    }
    private static void WriteZip(string file,string entry,string text)
    {
        using var stream=File.Create(file);using var zip=new ZipArchive(stream,ZipArchiveMode.Create);
        using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write(text);
    }
    private sealed class Files:IFileSystem
    {
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>path=="/def/original.sii";
        public bool DirectoryExists(string path)=>path.TrimEnd('/')=="/def";
        public IList<string> GetFiles(string path)=>DirectoryExists(path)?["/def/original.sii"]:[];
        public byte[] ReadAllBytes(string path)=>FileExists(path)?Encoding.UTF8.GetBytes("official"):throw new FileNotFoundException(path);
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
