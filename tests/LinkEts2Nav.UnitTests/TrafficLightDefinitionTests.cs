using System.Text;
using LinkEts2Nav.Map;
using LinkEts2Nav.Runtime;
using TruckLib;
using TruckLib.Models.Ppd;
using Semaphore=TruckLib.Models.Ppd.Semaphore;
using TruckLib.ScsMap;

namespace LinkEts2Nav.UnitTests;

public sealed class TrafficLightDefinitionTests
{
    [Theory]
    [InlineData(SemaphoreType.TrafficLight,true)]
    [InlineData(SemaphoreType.ModelOnly,false)]
    [InlineData(SemaphoreType.UseProfile,false)]
    public void CustomizedLayoutsRetainOnlyProvenOrdinaryControlsForLiveConfirmation(SemaphoreType type,bool ordinary)
    {
        var descriptor=new PrefabDescriptor{Nodes=[new(){Position=System.Numerics.Vector3.Zero},new(){Position=new(0,0,30)}],
            NavCurves=[new(){Length=10,SemaphoreId=0,StartRotation=System.Numerics.Quaternion.Identity}],
            Semaphores=[new(){Type=type,SemaphoreId=0,Rotation=System.Numerics.Quaternion.Identity,Intervals=new(15,2,23,1)}]};
        var prefab=new Prefab{CustomSemaphores=true,Nodes=[new Node{Position=System.Numerics.Vector3.Zero},new Node{Position=new(0,0,30)}]};
        var control=Assert.Single(TrafficSignalMap.Build(prefab,descriptor,new GameDataManager(new Files()).TrafficSignals,SpeedLimitData.Empty));
        if(!ordinary){Assert.Null(control.Timing);return;}
        Assert.True(control.Timing!.CustomLayout);
        Assert.Null(control.Timing.NextAfterRed);
        Assert.Contains("实时",control.Reason);
    }
    [Theory]
    [InlineData("1.0",SignalPhase.OrangeToGreen)]
    [InlineData("0.0",SignalPhase.Green)]
    public void RealSiiFloatQuadruplesUseScsQuaternionOrderForPhaseIntervals(string redAmber,SignalPhase expected)
    {
        var fs=new Files{Text=$$"""
            SiiNunit {
              tr_semaphore_profile : tr_sem_prof.base {
                type[]: traffic_light_minor
                interval[]: (24.0, 3.0, 32.0, {{redAmber}})
              }
              tr_semaphore_profile : tr_sem_prof.test { inherited: tr_sem_prof.base }
              tr_semaphore_profile : tr_sem_prof.test.germany { inherited: tr_sem_prof.test }
            }
            """};
        var manager=new GameDataManager(fs);
        var unit=Assert.Single(manager.GetSii("/def/world/semaphore_profile.sii").Named("tr_sem_prof.base"));
        object raw=unit.Attributes["interval"];
        var items=Assert.IsAssignableFrom<System.Collections.IEnumerable>(raw);
        var native=Assert.IsType<System.Numerics.Quaternion>(Assert.Single(items.Cast<object>()));
        Assert.Equal(24,native.W);Assert.Equal(3,native.X);Assert.Equal(32,native.Y);Assert.Equal(float.Parse(redAmber,System.Globalization.CultureInfo.InvariantCulture),native.Z);
        var result=manager.TrafficSignals.Resolve(new Prefab{SemaphoreProfile="test"},new Semaphore{Type=SemaphoreType.UseProfile},"germany",out _);
        Assert.Equal(expected,result?.NextAfterRed);
        Assert.Equal(1,fs.Reads);
    }
    [Theory]
    [InlineData(0,SignalPhase.Green)]
    [InlineData(2,SignalPhase.OrangeToGreen)]
    public void ExplicitNativeIntervalsDetermineThePhaseAfterRed(float preparation,SignalPhase expected)
    {
        var reader=new GameDataManager(new Files()).TrafficSignals;
        var signal=new Semaphore{Type=SemaphoreType.TrafficLight,Intervals=new(15,2,23,preparation)};
        Assert.Equal(expected,reader.Resolve(new Prefab(),signal,"germany",out _)?.NextAfterRed);
        signal.Intervals=new(0,2,23,preparation);
        Assert.Null(reader.Resolve(new Prefab(),signal,"germany",out _));
        signal.Type=SemaphoreType.ModelOnly;
        Assert.Null(reader.Resolve(new Prefab(),signal,"germany",out _));
    }
    [Fact]
    public void CountryOverridesInheritanceAndRepeatedGroupValuesAreReadOnceThroughTheManager()
    {
        var fs=new Files();fs.Text="""
            SiiNunit {
              tr_semaphore_profile : tr_sem_prof.base { type[]: traffic_light interval[]: (15, 2, 23, 0) }
              tr_semaphore_profile : tr_sem_prof.test { inherited: tr_sem_prof.base }
              tr_semaphore_profile : tr_sem_prof.test.germany { inherited: tr_sem_prof.test interval[]: (15, 2, 23, 2) }
            }
            """;
        var manager=new GameDataManager(fs);var reader=manager.TrafficSignals;
        var signal=new Semaphore{Type=SemaphoreType.UseProfile,SemaphoreId=5};
        var prefab=new Prefab{SemaphoreProfile="test"};
        Assert.Equal(SignalPhase.OrangeToGreen,reader.Resolve(prefab,signal,"germany",out _)?.NextAfterRed);
        Assert.Equal(SignalPhase.Green,reader.Resolve(prefab,signal,"france",out _)?.NextAfterRed);
        Assert.Null(reader.Resolve(prefab,signal,null,out _));
        manager.GetSii("/def/world/semaphore_profile.sii");
        Assert.Equal(1,fs.Reads);
    }
    [Fact]
    public void MissingTypesAndInheritanceCyclesNeverInventTheNextPhase()
    {
        var fs=new Files{Text="""
            SiiNunit {
              tr_semaphore_profile : tr_sem_prof.test { interval[]: (15, 2, 23, 2) inherited: tr_sem_prof.cycle }
              tr_semaphore_profile : tr_sem_prof.cycle { inherited: tr_sem_prof.test }
            }
            """};
        Assert.Null(new GameDataManager(fs).TrafficSignals.Resolve(new Prefab{SemaphoreProfile="test"},
            new Semaphore{Type=SemaphoreType.UseProfile},"germany",out _));
    }
    private sealed class Files:IFileSystem
    {
        private const string Name="/def/world/semaphore_profile.sii";
        public string? Text;public int Reads;
        public char DirectorySeparator=>'/';
        public bool FileExists(string path)=>path==Name&&Text is not null;
        public bool DirectoryExists(string path)=>path.TrimEnd('/')=="/def/world";
        public IList<string> GetFiles(string path)=>path.TrimEnd('/')=="/def/world"&&Text is not null?[Name]:[];
        public byte[] ReadAllBytes(string path){Reads++;return Encoding.UTF8.GetBytes(Text??throw new FileNotFoundException(path));}
        public Stream Open(string path)=>new MemoryStream(ReadAllBytes(path));
        public string ReadAllText(string path)=>Encoding.UTF8.GetString(ReadAllBytes(path));
        public string ReadAllText(string path,Encoding encoding)=>encoding.GetString(ReadAllBytes(path));
        public string GetParent(string path)=>path[..path.LastIndexOf('/')];
    }
}
