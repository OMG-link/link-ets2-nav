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
    public void LoadedPrefabWithoutNavigationCurvesExplainsTheConnectionFailure()
    {
        var fs=new Files();
        fs.Content["/def/world/prefab.test.sii"]="SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/port.ppd\" } }";
        fs.Content["/prefab/port.ppd"]="cached binary descriptor";
        var map=new TruckLib.ScsMap.Map();var node=new Node{Uid=10,Position=Vector3.Zero};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[node]};node.ForwardItem=prefab;
        map.Nodes.Add(node.Uid,node);map.MapItems.Add(prefab.Uid,prefab);
        using var data=new GameDataManager(fs,map:map);
        data.Projection("ppd","/prefab/port.ppd",()=>new TruckLib.Models.Ppd.PrefabDescriptor{NavCurves=[]});
        var failure=Assert.IsType<MapQueryFailure>(data.DescribeMissingConnection(map,node.Uid));
        Assert.Equal(MapQueryFailureKind.DefinitionUnavailable,failure.Kind);
        Assert.Contains("000000000000002A",failure.Reason);Assert.Contains("/prefab/port.ppd",failure.Reason);
        Assert.Contains("NavCurves=0",failure.Reason);
    }

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

    [Fact]
    public void PreparationSharesCatalogueWithWorldAndFeatureReaders()
    {
        var fs=new Files();
        fs.Content["/def/world/sign.sii"]="SiiNunit { sign_model : sign.test { model_desc: \"/model/test.pmd\" traffic_rule: \"\" } }";
        fs.Content["/def/company.sii"]="SiiNunit { company_permanent : company.permanent.test { name: \"Test Depot\" } }";
        fs.Content["/def/sign/templates.sii"]="SiiNunit { sign_template_def : template.test { path: \"/sign/test.sii\" } }";
        fs.Content["/def/sign/boards.sii"]="SiiNunit { sign_board_def : sign_board.test { path: \"/sign/board.sii\" } }";
        fs.Content["/sign/test.sii"]="SiiNunit { sign_template_board_binding : .binding { area_name: \"main\" linked_board: \"sign_board.test\" } }";
        fs.Content["/sign/board.sii"]="SiiNunit { sign_template_text : .text { id: 1 text: \"Test City\" } }";
        var data=new GameDataManager(fs);
        var definition=data.World.Signs["test"];
        data.Prepare(TestContext.Current.CancellationToken);
        long preparedParses=data.Statistics.SiiParses;
        Assert.Equal("Test Depot",data.Companies.ReadNames()["test"]);
        var native=new TruckLib.ScsMap.Map();var sign=Sign.Add(native,new(0,0,0),default);
        sign.SignTemplate="template.test";
        Assert.Equal(["Test City"],new SignTextReader(data).ReadTexts(sign));
        Assert.Equal(["Test City"],new SignTextReader(data).ReadTexts(sign));
        Assert.Equal(preparedParses+2,data.Statistics.SiiParses);
        Assert.Same(definition.Unit,Assert.Single(data.GetSii(definition.Resource).Named("sign.test")));
        Assert.All(fs.Reads,p=>Assert.Equal(1,p.Value));
        data.Prepare(TestContext.Current.CancellationToken);
        Assert.Equal(preparedParses+2,data.Statistics.SiiParses);
    }

    [Fact]
    public void LaterSectorPublicationPreservesPreviouslyQueriedObjectsAndNativeOwnership()
    {
        var native=new TruckLib.ScsMap.Map();var road=Road.Add(native,new(0,0,0),new(20,0,0),default);
        var data=new GameDataManager(new Files(),map:native);
        var first=new TruckLib.ScsMap.Map();var a=Sign.Add(first,new(4100,0,0),default);
        data.Map.Publish(first);
        var old=data.Map.GetItems<Sign>();
        var second=new TruckLib.ScsMap.Map();var b=Sign.Add(second,new(8100,0,0),default);
        data.Map.Publish(second);
        Assert.Same(a,data.Map.GetItem<Sign>(a.Uid));
        Assert.Same(b,data.Map.GetItem<Sign>(b.Uid));
        Assert.Same(road,data.Map.GetItem<Road>(road.Uid));
        Assert.Single(old);Assert.Equal(2,data.Map.GetItems<Sign>().Count);
        Assert.Single(data.Map.GetSector(new(1,0)).Items);
        Assert.Single(data.Map.GetSector(new(2,0)).Items);
        Assert.Single(native.MapItems);
    }

    [Fact]
    public void BrokenCataloguePropagatesItsCachedOriginalParsingFailure()
    {
        var fs=new Files();fs.Content["/def/sign/templates.sii"]="not a SII document";
        using var data=new GameDataManager(fs);
        var original=Record.Exception(()=>data.GetSii("/def/sign/templates.sii"));Assert.NotNull(original);
        Assert.Same(original,Record.Exception(()=>data.Prepare(TestContext.Current.CancellationToken)));
        Assert.Equal(1,fs.Reads["/def/sign/templates.sii"]);
        Assert.Empty(data.Diagnostics);
    }

    [Fact]
    public void CatalogueProjectionErrorsAreNotConvertedIntoMissingDefinitions()
    {
        var fs=new Files();fs.Content["/def/test.sii"]="SiiNunit { test : .one {} }";
        using var data=new GameDataManager(fs);var original=new NotSupportedException("unhandled projection");
        Assert.Same(original,Assert.Throws<NotSupportedException>(()=>data.TryDefinitions("/def/test.sii",_=>throw original)));
        Assert.Contains(nameof(CatalogueProjectionErrorsAreNotConvertedIntoMissingDefinitions),original.StackTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingConnectionSeparatesAbsentDescriptorFromReadFailure(bool absent)
    {
        var fs=new Files();fs.Content["/def/world/prefab.test.sii"]="SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/test.ppd\" } }";
        if(!absent)fs.Content["/prefab/test.ppd"]="present descriptor";
        var original=new IOException("asset read failed");fs.Failures["/prefab/test.ppd"]=original;
        var map=new TruckLib.ScsMap.Map();var node=new Node{Uid=10,Position=Vector3.Zero};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[node]};node.ForwardItem=prefab;
        map.Nodes.Add(10,node);map.MapItems.Add(42,prefab);
        using var data=new GameDataManager(fs,map:map);
        MapQueryIssue Query()=>data.DescribeMissingConnection(map,10);
        if(absent)
        {
            Assert.Contains("/prefab/test.ppd is missing",Assert.IsType<MapQueryFailure>(Query()).Reason);
            Assert.False(fs.Reads.ContainsKey("/prefab/test.ppd"));
        }
        else
        {
            Assert.Same(original,Assert.Throws<IOException>(()=>Query()));
            Assert.Contains(nameof(Files.ReadAllBytes),original.StackTrace);
        }
    }

    [Fact]
    public void CancelledPreparationCanBeRetried()
    {
        var data=new GameDataManager(new Files());
        using var stop=new CancellationTokenSource();stop.Cancel();
        Assert.Throws<OperationCanceledException>(()=>data.Prepare(stop.Token));
        Assert.Equal(0,data.Statistics.ResourceReads);
        data.Prepare(TestContext.Current.CancellationToken);Assert.NotNull(data.SpeedLimits);
    }

    private sealed class Files:IFileSystem
    {
        public readonly Dictionary<string,string> Content=new(StringComparer.Ordinal);
        public readonly Dictionary<string,Exception> Failures=new(StringComparer.Ordinal);
        public readonly ConcurrentDictionary<string,int> Reads=new(StringComparer.Ordinal),Scans=new(StringComparer.Ordinal);
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>Content.ContainsKey(path);
        public bool DirectoryExists(string path)=>Content.Keys.Any(p=>p.StartsWith(path.TrimEnd('/')+"/",StringComparison.Ordinal));
        public IList<string> GetFiles(string path)
        {Scans.AddOrUpdate(path,1,(_,n)=>n+1);return Content.Keys.Where(p=>GetParent(p)==path.TrimEnd('/')).ToArray();}
        public byte[] ReadAllBytes(string path)
        {Reads.AddOrUpdate(path,1,(_,n)=>n+1);if(Failures.TryGetValue(path,out var failure))throw failure;return Content.TryGetValue(path,out var text)?Encoding.UTF8.GetBytes(text):throw new FileNotFoundException(path);}
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
