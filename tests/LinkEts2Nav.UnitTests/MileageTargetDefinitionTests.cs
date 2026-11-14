using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
namespace LinkEts2Nav.UnitTests;

public sealed class MileageTargetDefinitionTests
{
    [Fact]
    public void ExactNamesVariantsAndDestinationNodeRemainSeparateFromRoadOwnership()
    {
        var data=new MileageTargetDefinitionReader(new Fs()).Read();
        var target=Assert.Single(data);
        Assert.Equal("Stykkishólmur",target.DefaultName);
        Assert.Equal("Stykkish.",Assert.Single(target.Variants).Text);
        Assert.Equal("is_stykkish2",target.Variants[0].Token);
        Assert.Equal(4675709586886761035UL,target.NodeUid);
        Assert.Equal(700,target.SearchRadius);
        Assert.Equal(new System.Numerics.Vector3(10,2,30),target.Position);
        Assert.Contains("\\x",target.RawDefaultName);
    }

    [Fact]
    public void InvalidUtf8DoesNotProduceAnInventedNameAndZeroArraysAreEmpty()
    {
        var fs=new Fs { Content="""
            SiiNunit
            {
            mileage_target : mileage.invalid {
              default_name: "Broken\xff"
              names: 0
              variants: 0
              node_uid: nil
              position: (&7f7fffff, &7f7fffff, &7f7fffff)
              search_radius: -1
            }
            }
            """ };
        var reader=new MileageTargetDefinitionReader(fs);var target=Assert.Single(reader.Read());
        Assert.Null(target.DefaultName);Assert.Empty(target.Variants);
        Assert.Null(target.NodeUid);Assert.Null(target.Position);
        Assert.Contains(reader.Diagnostics,d=>d.Contains("InvalidEscapedUtf8Name",StringComparison.Ordinal));
    }
    [Fact]
    public void LayoutTagsAreRemovedForSpeechButOriginalSpellingIsPreserved()
    {
        var fs=new Fs();fs.Content=fs.Content.Replace("Stykkish\\xc3\\xb3lmur","<font xscale=0.75>Stykkish\\xc3\\xb3lmur</font>",StringComparison.Ordinal);
        var target=Assert.Single(new MileageTargetDefinitionReader(fs).Read());
        Assert.Equal("Stykkishólmur",target.DefaultName);Assert.Contains("<font",target.RawDefaultName);
    }
    [Theory]
    [InlineData("Cluj-<br>Napoca","Cluj-Napoca")]
    [InlineData("Tuzla<br>Тузла","Tuzla Тузла")]
    public void LineBreaksPreserveWordBoundariesAndHyphenatedNames(string raw,string expected)
    {
        var fs=new Fs();fs.Content=fs.Content.Replace("Stykkish\\xc3\\xb3lmur",raw,StringComparison.Ordinal);
        var target=Assert.Single(new MileageTargetDefinitionReader(fs).Read());
        Assert.Equal(expected,target.DefaultName);Assert.Equal(raw,target.RawDefaultName);
    }
    private sealed class Fs:IFileSystem
    {
        public string Content="""
            SiiNunit
            {
            mileage_target : mileage.is_stykkisho {
              default_name: "Stykkish\xc3\xb3lmur"
              variants: 1
              variants[0]: is_stykkish2
              names: 1
              names[0]: "Stykkish."
              node_uid: 4675709586886761035
              position: (10, 2, 30)
              search_radius: 700
            }
            }
            """;
        public char DirectorySeparator=>'/';
        public bool FileExists(string p)=>p=="/def/sign/mileage_targets.sii";
        public bool DirectoryExists(string p)=>p.TrimEnd('/')=="/def/sign";
        public IList<string> GetFiles(string p)=>DirectoryExists(p)?["/def/sign/mileage_targets.sii"]:[];
        public byte[] ReadAllBytes(string p)=>Encoding.UTF8.GetBytes(Content);
        public string ReadAllText(string p)=>Content;
        public string ReadAllText(string p,Encoding encoding)=>Content;
        public Stream Open(string p)=>new MemoryStream(ReadAllBytes(p));
        public string GetParent(string p)=>p[..(p.LastIndexOf('/')+1)];
    }
}
