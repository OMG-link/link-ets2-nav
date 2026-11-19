using System.Numerics;
using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class MapReadDeduplicationTests
{
    private sealed class Files(string root):IFileSystem
    {
        public Dictionary<string,int> Reads {get;}=[];
        private string Local(string path)=>Path.Combine(root,path.TrimStart('/').Replace('/',Path.DirectorySeparatorChar));
        public char DirectorySeparator=>'/';
        public byte[] ReadAllBytes(string path){Reads[path]=Reads.GetValueOrDefault(path)+1;return File.ReadAllBytes(Local(path));}
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path));
        public bool FileExists(string path)=>File.Exists(Local(path));
        public bool DirectoryExists(string path)=>Directory.Exists(Local(path));
        public IList<string> GetFiles(string path)=>Directory.GetFiles(Local(path)).Select(f=>"/"+Path.GetRelativePath(root,f).Replace('\\','/')).ToArray();
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
        public string ReadAllText(string path)=>Encoding.UTF8.GetString(ReadAllBytes(path));
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
    }
    [Fact]
    public void AResourceReadsEachSectorFileOnceAcrossOverlappingAndRevisitedWindows()
    {
        string root=Path.Combine(Path.GetTempPath(),"map-read-cache-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var native=new TruckLib.ScsMap.Map();Road.Add(native,new(10,0,10),new(100,0,10),default);Road.Add(native,new(4100,0,10),new(4200,0,10),default);
            native.Save(Path.Combine(root,"map"),"europe");
            var files=new Files(root);using var data=new GameDataManager(files);
            data.ReadWindow([new(0,0)]);data.ReadWindow([new(0,0),new(1,0)]);data.ReadWindow([new(0,0)]);
            Assert.Contains(files.Reads.Keys,p=>p.EndsWith(".base"));Assert.All(files.Reads.Values,n=>Assert.Equal(1,n));
        }
        finally{Directory.Delete(root,true);}
    }
}
