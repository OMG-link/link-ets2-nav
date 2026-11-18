using System.Text;
using LinkEts2Nav.Map;
using TruckLib;

namespace LinkEts2Nav.UnitTests;

public sealed class CompanyPlaceReaderTests
{
    [Fact]
    public void AbsentAndUntranslatedDisplayNamesDoNotInventCompanyNames()
    {
        var fs=new Files();fs.Content["/def/company.sii"]="""
            SiiNunit {
              company_permanent : company.permanent.named { name: "  Stórmarkaður  " }
              company_permanent : company.permanent.absent { }
              company_permanent : company.permanent.empty { name: "" }
              company_permanent : company.permanent.untranslated { name: "@@company_name@@" }
            }
            """;
        var reader=new CompanyPlaceReader(fs);var names=reader.ReadNames();
        Assert.Equal("Stórmarkaður",names["named"]);Assert.False(names.ContainsKey("absent"));
        Assert.Null(names["empty"]);Assert.Null(names["untranslated"]);
        Assert.Equal(2,reader.Diagnostics.Count);Assert.Same(names,reader.ReadNames());
    }
    [Fact]
    public void MissingCatalogueIsLegitimatelyEmpty()
    {
        var reader=new CompanyPlaceReader(new Files());Assert.Empty(reader.ReadNames());Assert.Empty(reader.Diagnostics);
    }
    [Fact]
    public void ReferencedCatalogueReadFailureRetainsOriginalErrorAndStack()
    {
        var fs=new Files();fs.Content["/def/company.sii"]="present";
        var original=new IOException("company catalogue read failed");fs.Failure=original;
        var reader=new CompanyPlaceReader(fs);
        Assert.Same(original,Assert.Throws<IOException>(()=>reader.ReadNames()));
        Assert.Contains(nameof(Files.ReadAllBytes),original.StackTrace);
        Assert.Same(original,Assert.Throws<IOException>(()=>reader.ReadNames()));
        Assert.Empty(reader.Diagnostics);
    }
    [Fact]
    public void MalformedCatalogueDoesNotBecomeSuccessfulPartialNames()
    {
        var fs=new Files();fs.Content["/def/company.sii"]="SiiNunit";
        using var data=new GameDataManager(fs);
        var original=Record.Exception(()=>data.GetSii("/def/company.sii"));Assert.NotNull(original);
        Assert.Same(original,Record.Exception(()=>new CompanyPlaceReader(data).ReadNames()));
    }
    private sealed class Files:IFileSystem
    {
        public Dictionary<string,string> Content {get;}=new();
        public Exception? Failure {get;set;}
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>Content.ContainsKey(path);
        public bool DirectoryExists(string path)=>Content.Keys.Any(p=>p.StartsWith(path.TrimEnd('/')+"/",StringComparison.Ordinal));
        public IList<string> GetFiles(string path)=>Content.Keys.Where(p=>GetParent(p)==path.TrimEnd('/')).ToArray();
        public byte[] ReadAllBytes(string path)=>Failure is {} error?throw error:Encoding.UTF8.GetBytes(Content[path]);
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
