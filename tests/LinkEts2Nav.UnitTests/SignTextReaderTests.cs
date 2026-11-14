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
        sign.SignTemplate="sign_templ.missing";
        Assert.Empty(reader.ReadTexts(sign));Assert.Equal("TemplateNotIndexed",Assert.Single(reader.Diagnostics).Reason);
    }
    private sealed class Fs:IFileSystem
    {
        private readonly Dictionary<string,string> files=new()
        {
            ["/def/sign/templates.sii"]="SiiNunit {\n sign_template_def : sign_templ.test {\n path: \"/def/sign/test.sii\"\n }\n}",
            ["/def/sign/boards.sii"]="SiiNunit {\n sign_board_def : sign_board.test {\n path: \"/def/sign/board.sii\"\n }\n}",
            ["/def/sign/test.sii"]="SiiNunit {\n sign_template_board_binding : .binding {\n area_name: area\n linked_board: sign_board.test\n }\n}",
            ["/def/sign/board.sii"]="SiiNunit {\n sign_template_text : .one {\n id: 1\n text: \"München\"\n }\n sign_template_text : .two {\n id: 2\n text: \"Salzburg\"\n }\n sign_template_text : .default {\n id: 3\n text: \"Name\"\n }\n}"
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
