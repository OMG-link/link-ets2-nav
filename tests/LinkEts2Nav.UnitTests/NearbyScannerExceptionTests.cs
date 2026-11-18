using System.Numerics;
using System.Reflection;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class NearbyScannerExceptionTests
{
    [Fact]
    public void ReferencedPrefabReadFailureRetainsOriginalException()
    {
        var error=new IOException("prefab read failed");
        var fs=new Files("/prefab/test.ppd",error);
        fs.Content["/def/world/prefab.test.sii"]=Encoding.UTF8.GetBytes("SiiNunit { prefab_model : prefab.test { prefab_desc: \"/prefab/test.ppd\" } }");
        var map=new TruckLib.ScsMap.Map();var node=new Node{Uid=10,Position=Vector3.Zero};
        var prefab=new Prefab{Uid=42,Model=new Token("test"),Nodes=[node]};
        map.Nodes.Add(10,node);map.MapItems.Add(42,prefab);
        using var data=new GameDataManager(fs,map:map);var coverage=MapScanCoverage.Create(Vector3.Zero,100,null,null,0);
        // Model an already decoded native window, so this checks the descriptor
        // operation itself without depending on the sector binary decoder.
        Set(data,"loadedWindow",map);Set(data,"loadedSectors",coverage.Sectors.ToArray());
        var scanner=new NearbyCameraScanner(data);
        Assert.Same(error,Assert.Throws<IOException>(()=>scanner.ReadNearby(Vector3.Zero,100,coverage:coverage)));
        Assert.Contains(nameof(Files.ReadAllBytes),error.StackTrace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocatorReadFailureIsNotConvertedIntoAnEmptyCameraResult(bool missing)
    {
        Exception error=missing?new FileNotFoundException("referenced model missing"):new ArgumentException("unknown reader failure");
        var (scanner,data)=ModelScanner(new Files("/model/test.pmg",error));
        using(data)
        {
            Assert.Same(error,Record.Exception(()=>scanner.ReadNearby(Vector3.Zero,100)));
            Assert.Contains(nameof(Files.ReadAllBytes),error.StackTrace);
        }
    }

    [Fact]
    public void MalformedLocatorHeaderIsNotReportedAsSuccessfulPartialScan()
    {
        var fs=new Files();fs.Content["/model/test.pmg"]=new byte[112];
        var (scanner,data)=ModelScanner(fs);
        using(data)Assert.Throws<InvalidDataException>(()=>scanner.ReadNearby(Vector3.Zero,100));
    }

    [Fact]
    public void ValidModelWithoutCameraLocatorsIsStillLegitimatelyEmpty()
    {
        var fs=new Files();var header=new byte[112];header[0]=0x15;header[1]=(byte)'g';header[2]=(byte)'m';header[3]=(byte)'P';
        fs.Content["/model/test.pmg"]=header;
        var (scanner,data)=ModelScanner(fs);
        using(data)Assert.Empty(scanner.ReadNearby(Vector3.Zero,100).Cameras);
    }

    private static (NearbyCameraScanner,GameDataManager) ModelScanner(Files fs)
    {
        fs.Content["/def/world/sign.test.sii"]=Encoding.UTF8.GetBytes("SiiNunit { sign_model : sign.test { model_desc: \"/model/test.pmd\" } }");
        var map=new TruckLib.ScsMap.Map();var sign=Sign.Add(map,Vector3.Zero,new Token("test"));
        var data=new GameDataManager(fs,map:map);var scanner=new NearbyCameraScanner(data);
        // Exercise a repeated scan against a previously published window. Only
        // the model read can fail here; no unrelated topology is fabricated.
        Set(scanner,"map",map);Set(scanner,"loadedSectors",MapScanCoverage.Create(Vector3.Zero,100,null,null,0).Sectors.ToArray());
        return(scanner,data);
    }
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(target,value);
    private sealed class Files(string? failingPath=null,Exception? failure=null):IFileSystem
    {
        public Dictionary<string,byte[]> Content {get;}=new();
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>Content.ContainsKey(path);
        public bool DirectoryExists(string path)=>Content.Keys.Any(p=>p.StartsWith(path.TrimEnd('/')+"/",StringComparison.Ordinal));
        public IList<string> GetFiles(string path)=>Content.Keys.Where(p=>GetParent(p)==path.TrimEnd('/')).ToArray();
        public byte[] ReadAllBytes(string path)=>path==failingPath?throw failure!:Content.TryGetValue(path,out var bytes)?bytes:throw new FileNotFoundException(path);
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path),false);
        public string ReadAllText(string path)=>ReadAllText(path,Encoding.UTF8);
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
