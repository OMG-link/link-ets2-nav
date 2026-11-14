using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;

public sealed class GameDataManagerTests
{
    [Fact]
    public async Task SharedSiiAndIncludesAreReadAndParsedOnceAcrossConcurrentQueries()
    {
        var fs=new Files();
        fs.Content["/def/world/models.sii"]="SiiNunit\n{\n@include \"shared.sui\"\n}\n";
        fs.Content["/def/world/shared.sui"]="model_def : model.a\n{\nmodel_desc: \"/model/a.pmd\"\n}\n";
        var data=new GameDataManager(fs);
        var documents=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>data.GetSii("/def/world/./models.sii"))));
        Assert.All(documents,d=>Assert.Same(documents[0],d));
        Assert.Same(documents[0],data.GetSii("def\\world\\models.sii"));
        Assert.Equal("model.a",Assert.Single(documents[0].OfClass("model_def")).Name);
        Assert.Single(documents[0].Named("model.a"));
        Assert.Equal(1,fs.Reads["/def/world/models.sii"]);
        Assert.Equal(1,fs.Reads["/def/world/shared.sui"]);
        Assert.Equal(1,data.Statistics.SiiParses);
    }

    [Fact]
    public void DirectoryAndFailedResourceLookupsAreNotRepeated()
    {
        var fs=new Files();var data=new GameDataManager(fs);
        data.GetFiles("/def/world/");data.GetFiles("def/world");
        Assert.Equal(1,fs.Scans["/def/world"]);
        Assert.Throws<FileNotFoundException>(()=>data.GetSii("/missing.sii"));
        Assert.Throws<FileNotFoundException>(()=>data.GetSii("missing.sii"));
        Assert.Equal(1,fs.Reads["/missing.sii"]);
    }

    [Fact]
    public void MapQueriesCombineNativeObjectsAndAdditionalSectorData()
    {
        var native=new TruckLib.ScsMap.Map();var road=Road.Add(native,new(3990,0,0),new(4010,0,0),default);
        var data=new GameDataManager(new Files(),map:native);
        var extra=new TruckLib.ScsMap.Map();var sign=Sign.Add(extra,new(4100,0,0),default);
        data.Map.Publish(extra);
        Assert.Same(road,data.Map.GetItem<Road>(road.Uid));
        Assert.Same(sign,data.Map.GetItem<Sign>(sign.Uid));
        Assert.Same(road.Node,data.Map.GetNode(road.Node.Uid));
        Assert.Same(sign.Node,data.Map.GetNode(sign.Node.Uid));
        Assert.Equal(2,data.Map.GetSector(new(1,0)).Items.Count);
        Assert.Same(sign,Assert.Single(data.Map.GetItems<Sign>()));
        Assert.Single(native.MapItems);Assert.Single(extra.MapItems);
    }

    [Fact]
    public void StandaloneReadersCanShareTheSameDatasetScope()
    {
        var source=new Files();
        Assert.Same(GameDataManager.From(source),GameDataManager.From(source));
    }

    private sealed class Files:IFileSystem
    {
        public readonly Dictionary<string,string> Content=new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string,int> Reads=new(StringComparer.Ordinal),Scans=new(StringComparer.Ordinal);
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>Content.ContainsKey(path);
        public bool DirectoryExists(string path)=>Content.Keys.Any(p=>p.StartsWith(path.TrimEnd('/')+"/",StringComparison.Ordinal));
        public IList<string> GetFiles(string path)
        {Scans.AddOrUpdate(path,1,(_,n)=>n+1);return Content.Keys.Where(p=>GetParent(p)==path.TrimEnd('/')).ToArray();}
        public byte[] ReadAllBytes(string path)
        {Reads.AddOrUpdate(path,1,(_,n)=>n+1);return Content.TryGetValue(path,out var text)?Encoding.UTF8.GetBytes(text):throw new FileNotFoundException(path);}
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
