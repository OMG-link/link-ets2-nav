using System.Text.Json;
using LinkEts2Nav.Speech;

namespace LinkEts2Nav.UnitTests;

public sealed class ContextualSpeechTests : IDisposable
{
    private const string Carrier="五百米后，进入巴黎城区。";
    private readonly string root=Path.Combine(Path.GetTempPath(),"nav-context-"+Guid.NewGuid().ToString("N"));
    private static byte[] Pcm()=>Enumerable.Range(0,100000).Select(i=>(byte)(i%200)).ToArray();
    private static ContextFragment Fragment(string text)=>new(text,Carrier,["巴黎"],Carrier.IndexOf(text,StringComparison.Ordinal),Carrier.IndexOf(text,StringComparison.Ordinal)+text.Length);
    private static ContextTimestamps Timestamps(byte[] pcm,string[]? text=null)
    {
        text??=["五","百","米","后","，进","入","巴","黎","城","区。"];
        var words=text.Select((t,i)=>new{text=t,begin_index=i,end_index=i+1,begin_time=80+i*80,end_time=160+i*80}).ToArray();
        var output=JsonSerializer.SerializeToElement(new{type="sentence-end",original_text=Carrier,sentence=new{index=0,words}});
        return new(1,Carrier,SpeechPack.Hash(pcm),[new(output,pcm.Length/2)]);
    }

    [Fact]
    public void MapsPunctuationAttachedWordsByExactTextInsteadOfSpokenCharacterIndex()
    {
        var pcm=Pcm();var cut=ContextualSpeech.Locate(Fragment("五百米后，进入"),Timestamps(pcm),pcm);
        Assert.Equal(0,cut.StartSample);Assert.Equal(560*24,cut.EndSample);
        var tail=ContextualSpeech.Locate(Fragment("城区。"),Timestamps(pcm),pcm);
        Assert.Equal(720*24,tail.StartSample);Assert.Equal(pcm.Length/2,tail.EndSample);
    }

    [Fact]
    public void RejectsChangedAudioIncompleteTextAndCutsInsideWord()
    {
        var pcm=Pcm();var timestamps=Timestamps(pcm);
        Assert.Throws<InvalidDataException>(()=>ContextualSpeech.Locate(Fragment("五百米后，进入"),timestamps,new byte[pcm.Length]));
        Assert.Throws<InvalidDataException>(()=>ContextualSpeech.Locate(Fragment("五百米后，进入"),Timestamps(pcm,["五","百"]),pcm));
        Assert.Throws<InvalidDataException>(()=>ContextualSpeech.Locate(Fragment("百米后，进入"),Timestamps(pcm,["五百米","后，","进入","巴黎","城区。"]),pcm));
    }

    [Fact]
    public void RejectsMultipleSentencesInsteadOfGuessingTimeOffsets()
    {
        var pcm=Pcm();var timestamps=Timestamps(pcm);
        Assert.Throws<InvalidDataException>(()=>ContextualSpeech.Locate(Fragment("五百米后，进入"),timestamps with {Events=[..timestamps.Events,..timestamps.Events]},pcm));
    }

    [Fact]
    public async Task OfflineExportSeparatesCompleteSentencesFromAllFragmentTargets()
    {
        await ContextualSpeech.RunAsync(["--export-context-jobs","--output",root]);
        var plan=ContextualSpeech.ReadPlan();Assert.Equal(25,plan.Items.Length);
        Assert.All(plan.Items,i=>Assert.EndsWith("。",i.CarrierText));
        var direct=File.ReadAllLines(Path.Combine(root,"direct-texts.txt"));
        Assert.Equal(231,direct.Length);Assert.DoesNotContain(plan.Items,i=>direct.Contains(i.Text));
        Assert.Contains("请注意红灯即将变绿。",direct);
        Assert.Equal(22,File.ReadAllLines(Path.Combine(root,"carrier-texts.txt")).Length);
    }

    [Fact]
    public async Task OfflineReslicingOverridesValidButStaleTargetCacheAndRecordsProvenance()
    {
        string sources=Path.Combine(root,"source"),fragments=Path.Combine(root,"fragments"),planFile=Path.Combine(root,"plan.json");
        Directory.CreateDirectory(root);var fragment=Fragment("五百米后，进入");var pcm=Pcm();
        await File.WriteAllTextAsync(planFile,JsonSerializer.Serialize(new ContextPlan(1,[fragment])));
        await SpeechPackBuilder.BuildAsync(sources,"model","voice",[Carrier],(_,_)=>Task.FromResult(pcm),cancellationToken:TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sources,SpeechPack.FileName(Carrier)+".timestamps.json"),JsonSerializer.Serialize(Timestamps(pcm)));
        await SpeechPackBuilder.BuildAsync(fragments,"model","voice",[fragment.Text],(_,_)=>Task.FromResult(new byte[]{1,0}),cancellationToken:TestContext.Current.CancellationToken);
        await ContextualSpeech.RunAsync(["--slice-context-pack","--plan",planFile,"--carriers",sources,"--output",fragments]);
        Assert.Equal(pcm[..(560*24*2)],PcmAudio.Read(Path.Combine(fragments,SpeechPack.FileName(fragment.Text))));
        using var provenance=JsonDocument.Parse(File.ReadAllText(Path.Combine(fragments,"context-provenance.json")));
        Assert.Equal(560*24,provenance.RootElement[0].GetProperty("EndSample").GetInt32());
        Assert.True(File.Exists(Path.Combine(sources,SpeechPack.FileName(Carrier))));
    }

    [Fact]
    public async Task NamedCompositionActuallyUsesPreparedContextFragmentsInsteadOfLocalVoice()
    {
        string pack=Path.Combine(root,"pack"),cache=Path.Combine(root,"cache");
        await SpeechPackBuilder.BuildAsync(pack,"model","voice",["五百米后，进入","城区，请注意减速慢行。"],
            (text,_)=>Task.FromResult(text=="五百米后，进入"?new byte[]{1,0}:new byte[]{3,0}),cancellationToken:TestContext.Current.CancellationToken);
        await SpeechPackBuilder.BuildAsync(Path.Combine(pack,"CityNames"),"model","voice",["巴黎"],
            (_,_)=>Task.FromResult(new byte[]{2,0}),cancellationToken:TestContext.Current.CancellationToken);
        var result=new SpeechPack(pack,cache).Generate("五百米后，进入巴黎城区，请注意减速慢行。",
            _=>throw new InvalidOperationException("Prepared fragments must not call local synthesis."));
        Assert.Equal(new byte[]{1,0,2,0,3,0},PcmAudio.Read(result.Path));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
