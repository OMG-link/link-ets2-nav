using System.Text;
using LinkEts2Nav.Map;
using TruckLib;
using TruckLib.ScsMap;
namespace LinkEts2Nav.UnitTests;
public sealed class SignTextReaderTests
{
    [Fact]
    public void MultipleDestinationTextsAndRawPlaceholderAreRetainedWithoutChoosingOneCity()
    {
        var fs=new Fs();
        var map=new TruckLib.ScsMap.Map();var sign=Sign.Add(map,System.Numerics.Vector3.Zero,default);
        sign.SignTemplate="sign_templ.test";
        var reader=new SignTextReader(fs);
        Assert.Equal(new[]{"München","Salzburg"},reader.ReadTexts(sign));
        Assert.Contains("Name",reader.ReadRawTexts(sign));Assert.Empty(reader.Diagnostics);
        Assert.DoesNotContain("Ulm",reader.ReadTexts(sign));
        Assert.Contains("Ulm",reader.ReadInventoryTexts(sign));
        sign.SignTemplate="sign_templ.missing";
        Assert.Empty(reader.ReadTexts(sign));Assert.Equal("TemplateNotIndexed",Assert.Single(reader.Diagnostics).Reason);
    }
    [Fact]
    public void NativeTextOpacityOverridesRemoveHiddenLayoutLabelsButKeepRawInventory()
    {
        var fs=new Fs();
        fs.Files["/def/sign/board.sii"]="""
            SiiNunit {
              sign_template_text : .kind { id: 1 text: "Stadt" font_opacity: 1 }
              sign_template_text : .old { id: 2 text: "Werlte" font_opacity: 1 }
              sign_template_text : .district { id: 3 text: "Landkreis Emsland" font_opacity: 1 }
              sign_template_text : .unused { id: 4 text: "Garmisch-Partenkirchen" font_opacity: 0 }
              sign_template_text : .custom { id: 5 text: "Name" font_opacity: 0 }
            }
            """;
        var map=new TruckLib.ScsMap.Map();var sign=Sign.Add(map,System.Numerics.Vector3.Zero,default);
        sign.SignTemplate="sign_templ.test";
        sign.SignOverrides=[
            new(){AreaName="area",Id=1,Attributes=[new SignOverrideAttribute<float>{Index=4,Value=0}]},
            new(){AreaName="area",Id=2,Attributes=[new SignOverrideAttribute<string>{Index=0,Value="Nürnberg"},new SignOverrideAttribute<float>{Index=4,Value=0}]},
            new(){AreaName="area",Id=3,Attributes=[new SignOverrideAttribute<float>{Index=4,Value=0}]},
            new(){AreaName="area",Id=5,Attributes=[new SignOverrideAttribute<string>{Index=0,Value="Nürnberg"},new SignOverrideAttribute<float>{Index=4,Value=1}]}];
        var reader=new SignTextReader(fs);
        Assert.Equal(new[]{"Nürnberg"},reader.ReadTexts(sign));
        Assert.Empty(reader.Diagnostics);
        Assert.Contains("Stadt",reader.ReadRawTexts(sign));
        Assert.Contains("Garmisch-Partenkirchen",reader.ReadInventoryTexts(sign));
        Assert.Equal("Nürnberg",new CitySignNameReader(GameDataManager.From(fs)).Read(sign));
    }

    [Theory]
    [InlineData("area",1,0f,false)]
    [InlineData("area",1,.25f,true)]
    [InlineData("other",1,0f,true)]
    [InlineData("area",99,0f,true)]
    public void VisibilityUsesTheTextObjectAndBoundAreaOnly(string area,uint id,float opacity,bool visible)
    {
        var fs=new Fs();fs.Files["/def/sign/board.sii"]="""SiiNunit { sign_template_text : .one { id: 1 text: "Nürnberg" font_opacity: 1 } }""";
        var map=new TruckLib.ScsMap.Map();var sign=Sign.Add(map,System.Numerics.Vector3.Zero,default);sign.SignTemplate="sign_templ.test";
        sign.SignOverrides=[new(){AreaName=area,Id=id,Attributes=[new SignOverrideAttribute<float>{Index=4,Value=opacity}]}];
        Assert.Equal(visible,new SignTextReader(fs).ReadTexts(sign).Count==1);
    }

    [Fact]
    public void UnknownOpacityTypeIsDiagnosedAndNeverReinterpretedAsAnImageOrIntegerAttribute()
    {
        var fs=new Fs();fs.Files["/def/sign/board.sii"]="""SiiNunit { sign_template_text : .one { id: 1 text: "Nürnberg" font_opacity: 1 } }""";
        var map=new TruckLib.ScsMap.Map();var sign=Sign.Add(map,System.Numerics.Vector3.Zero,default);sign.SignTemplate="sign_templ.test";
        sign.SignOverrides=[new(){AreaName="area",Id=1,Attributes=[new SignOverrideAttribute<int>{Index=4,Value=1}]}];
        var reader=new SignTextReader(fs);Assert.Empty(reader.ReadTexts(sign));
        Assert.Contains(reader.Diagnostics,d=>d.Reason=="UnknownTextOpacity");
        Assert.Contains("Nürnberg",reader.ReadRawTexts(sign));
    }

    private sealed class Fs:IFileSystem
    {
        public Dictionary<string,string> Files=>files;
        private readonly Dictionary<string,string> files=new()
        {
            ["/def/sign/templates.sii"]="SiiNunit {\n sign_template_def : sign_templ.test {\n path: \"/def/sign/test.sii\"\n }\n}",
            ["/def/sign/boards.sii"]="SiiNunit {\n sign_board_def : sign_board.test {\n path: \"/def/sign/board.sii\"\n }\n}",
            ["/def/sign/test.sii"]="SiiNunit {\n sign_template_board_binding : .binding {\n area_name: area\n linked_board: sign_board.test\n }\n}",
            ["/def/sign/board.sii"]="SiiNunit {\n sign_template_text : .one {\n id: 1\n text: \"München\"\n }\n sign_template_text : .two {\n id: 2\n text: \"Salzburg\"\n }\n sign_template_text : .default {\n id: 3\n text: \"Name\"\n }\n sign_template_mileage : .mileage {\n id: 4\n target: \"\"\n display: name\n text: \"Ulm\"\n }\n}"
        };
        public char DirectorySeparator=>'/';
        public bool FileExists(string p)=>files.ContainsKey(p);
        public bool DirectoryExists(string p)=>files.Keys.Any(k=>k.StartsWith(p.TrimEnd('/')+"/"));
        public IList<string> GetFiles(string p)=>files.Keys.Where(k=>GetParent(k).TrimEnd('/')==p.TrimEnd('/')).ToArray();
        public byte[] ReadAllBytes(string p)=>Encoding.UTF8.GetBytes(files[p]);
        public string ReadAllText(string p)=>files[p];
        public string ReadAllText(string p,Encoding encoding)=>files[p];
        public Stream Open(string p)=>new MemoryStream(ReadAllBytes(p));
        public string GetParent(string p)=>p[..(p.LastIndexOf('/')+1)];
    }
}
