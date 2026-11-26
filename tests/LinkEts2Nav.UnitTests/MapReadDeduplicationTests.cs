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

    [Fact]
    public void SectorFilesAndModelMeshesShareTheByteBudgetAndPreserveOpenStreamsAndDefinitions()
    {
        string root=Path.Combine(Path.GetTempPath(),"sector-byte-cache-"+Guid.NewGuid().ToString("N"));
        string Write(string path,params byte[] content)
        {
            var local=Path.Combine(root,path.TrimStart('/'));Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllBytes(local,content);return path;
        }
        try
        {
            var a=Write("/map/custom/sec+0000+0000.base",1,2,3,4);
            var b=Write("/map/custom/sec+0001+0000.aux",5,6,7,8);
            var c=Write("/model/landmark.pmg",9,10,11,12);
            var oversized=Write("/model/distant.pmg",new byte[9]);
            var fixedData=Write("/def/shared.data",1,2,3);
            var files=new Files(root);using var data=new GameDataManager(files);
            var store=new GameResourceStore(data,()=>files,"/map/custom.mbd",rawFileBudget:8);
            const string missing="/map/custom/sec+0004+0000.snd";
            var failure=Assert.Throws<FileNotFoundException>(()=>store.Open(missing));
            Parallel.For(0,16,_=>{using var reader=store.Open(a);Assert.Equal(1,reader.ReadByte());});
            Assert.Equal(1,files.Reads[a]);
            using var first=store.Open(a);
            store.Read(fixedData);store.Read(b);store.Read(a);store.Read(c);
            Assert.Equal(11,store.ResidentBytes);
            store.Read(oversized);store.Read(oversized);
            Assert.Equal(11,store.ResidentBytes);Assert.Equal(2,files.Reads[oversized]);
            store.Read(a);store.Read(c);
            Assert.Equal(1,files.Reads[a]);Assert.Equal(1,files.Reads[c]);
            store.Read(b);Assert.Equal(2,files.Reads[b]);
            store.Read(a);Assert.Equal(2,files.Reads[a]);
            var held=new byte[4];first.ReadExactly(held);Assert.Equal(new byte[]{1,2,3,4},held);
            store.Read(fixedData);Assert.Equal(1,files.Reads[fixedData]);Assert.Equal(11,store.ResidentBytes);
            Assert.Same(failure,Assert.Throws<FileNotFoundException>(()=>store.Open(missing)));
            Assert.Equal(1,files.Reads[missing]);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void RevisitingAnEvictedNativeSectorReconstructsTheSameMapObjects()
    {
        string root=Path.Combine(Path.GetTempPath(),"map-window-eviction-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var native=new TruckLib.ScsMap.Map();var road=Road.Add(native,new(10,0,10),new(100,0,10),default);
            Road.Add(native,new(4100,0,10),new(4200,0,10),default);native.Save(Path.Combine(root,"map"),"mod");
            var files=new Files(root);using var data=new GameDataManager(files);
            var parts=Directory.GetFiles(Path.Combine(root,"map","mod"),"sec+0000+0000.*");
            int oneSector=checked((int)parts.Sum(p=>new FileInfo(p).Length));
            var store=new GameResourceStore(data,()=>files,"/map/mod.mbd",oneSector);
            var first=TruckLib.ScsMap.Map.Open("/map/mod.mbd",store,[new(0,0)]);
            _=TruckLib.ScsMap.Map.Open("/map/mod.mbd",store,[new(1,0)]);
            var revisited=TruckLib.ScsMap.Map.Open("/map/mod.mbd",store,[new(0,0)]);
            Assert.True(first.MapItems.ContainsKey(road.Uid));Assert.True(revisited.MapItems.ContainsKey(road.Uid));
            Assert.Equal(2,files.Reads["/map/mod/sec+0000+0000.base"]);Assert.Equal(1,files.Reads["/map/mod.mbd"]);
            Assert.InRange(store.ResidentBytes,0,oneSector+new FileInfo(Path.Combine(root,"map","mod.mbd")).Length);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void AReadWindowPreservesNavigationItemsAndBorrowedTerrain()
    {
        string root=Path.Combine(Path.GetTempPath(),"map-window-items-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var native=new TruckLib.ScsMap.Map();
            var borrowed=Road.Add(native,new(10,0,10),new(100,0,10),default,16,10);
            var road=Road.Add(native,new(10,0,30),new(100,0,30),default,16,10);
            var descriptor=new TruckLib.Models.Ppd.PrefabDescriptor
            {
                Nodes=[new(){Position=Vector3.Zero,Direction=-Vector3.UnitZ}]
            };
            var prefab=Prefab.Add(native,new(200,0,60),"prefab",descriptor);
            var decoration=Model.Add(native,new(200,0,10),"decor",default,default);decoration.ModelHookups=false;
            var camera=Model.Add(native,new(200,0,20),"camera",default,default);camera.ModelHookups=true;
            var sign=Sign.Add(native,new(200,0,30),"sign");
            var compound=Compound.Add(native,new(200,0,40));
            var nested=Sign.Add(compound,new(199,0,40),"nested");
            native.Save(Path.Combine(root,"map"),"europe");
            var host=new TruckLib.ScsMap.Map();host.MapItems.Add(borrowed.Uid,borrowed);
            host.Nodes.Add(borrowed.Node.Uid,borrowed.Node);host.Nodes.Add(borrowed.ForwardNode.Uid,borrowed.ForwardNode);
            using var data=new GameDataManager(new Files(root),map:host);
            var window=data.ReadWindow([new(0,0)]);
            var local=Assert.IsType<Road>(window.MapItems[road.Uid]);
            Assert.Null(local.Left.Terrain);Assert.Null(local.Right.Terrain);
            Assert.Same(window.Nodes[road.Node.Uid],local.Node);
            var junction=Assert.IsType<Prefab>(window.MapItems[prefab.Uid]);
            Assert.All(junction.PrefabNodes,n=>Assert.Null(n.Terrain));
            Assert.Same(window.Nodes[prefab.Nodes[0].Uid],junction.Nodes[0]);
            Assert.Same(borrowed,window.MapItems[borrowed.Uid]);Assert.NotNull(borrowed.Left.Terrain);
            Assert.False(window.MapItems.ContainsKey(decoration.Uid));Assert.True(window.Nodes.ContainsKey(decoration.Node.Uid));
            Assert.True(Assert.IsType<Model>(window.MapItems[camera.Uid]).ModelHookups);
            Assert.IsType<Sign>(window.MapItems[sign.Uid]);
            Assert.Contains(LoadedMapItems.Enumerate(window.MapItems.Values),i=>i is Sign && i.Uid==nested.Uid);
        }
        finally{Directory.Delete(root,true);}
    }
}
