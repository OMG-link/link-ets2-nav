using System.Text.Json;
using LinkEts2Nav.Runtime;
using LinkEts2Nav.Speech;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinkEts2Nav.UnitTests;

public sealed class SpeechPackSettingsTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"nav-pack-settings-"+Guid.NewGuid().ToString("N"));
    private string SettingsFile=>Path.Combine(root,"Settings","LinkEts2Nav.Speech.json");
    private async Task<string> Pack(string name,string text="左转。")
    {
        string path=Path.Combine(root,name);
        await SpeechPackBuilder.BuildAsync(path,"model","voice",[text],(_,_)=>Task.FromResult(new byte[]{1,0,2,0}),cancellationToken:TestContext.Current.CancellationToken);
        return path;
    }
    [Fact]
    public async Task SavesRelativePackDirectoryAndPreservesAllOtherSettings()
    {
        await Pack("new-pack");Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        await File.WriteAllTextAsync(SettingsFile,"{\"enabled\":true,\"modelDirectory\":\"models\",\"workerPath\":\"tools/worker.dll\",\"threads\":4,\"voice\":\"zf_067\",\"extraFutureSetting\":{\"value\":3}}",TestContext.Current.CancellationToken);
        var service=new SpeechPackSettings(root);var selection=await service.SaveAsync("new-pack",TestContext.Current.CancellationToken);
        using var document=JsonDocument.Parse(File.ReadAllText(SettingsFile));
        Assert.Equal("new-pack",document.RootElement.GetProperty("packDirectory").GetString());
        Assert.Equal(4,document.RootElement.GetProperty("threads").GetInt32());
        Assert.Equal(3,document.RootElement.GetProperty("extraFutureSetting").GetProperty("value").GetInt32());
        Assert.Equal(Path.Combine(root,"new-pack"),selection.Directory);Assert.Single(selection.Fixed);Assert.Empty(selection.Names);
        Assert.Null(service.Read().ActiveDirectory);Assert.Equal(selection,service.Pending);
    }
    [Fact]
    public async Task InvalidPackNeverChangesExistingSettingsOrPendingSelection()
    {
        await Pack("valid");var service=new SpeechPackSettings(root);
        var prior=await service.SaveAsync("valid",TestContext.Current.CancellationToken);string before=File.ReadAllText(SettingsFile);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(()=>service.SaveAsync("missing",TestContext.Current.CancellationToken));
        Assert.Equal(before,File.ReadAllText(SettingsFile));Assert.Same(prior,service.Pending);
        string file=prior.Fixed.Values.Single();File.AppendAllText(file,"damaged");
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.SaveAsync("valid",TestContext.Current.CancellationToken));
        Assert.Equal(before,File.ReadAllText(SettingsFile));
    }
    [Fact]
    public async Task NamesOnlyPackIsAcceptedAndWrongPcmFormatIsRejected()
    {
        string names=await Pack(Path.Combine("names-only","CityNames"),"Paris");var service=new SpeechPackSettings(root);
        var selection=await service.SaveAsync("names-only",TestContext.Current.CancellationToken);
        Assert.Empty(selection.Fixed);Assert.Single(selection.Names);
        string file=selection.Names.Values.Single();byte[] wav=File.ReadAllBytes(file);wav[24]=0;wav[25]=0;File.WriteAllBytes(file,wav);
        var manifest=JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Path.Combine(names,"manifest.json")))!;
        manifest=manifest with{Entries=[manifest.Entries[0] with{Sha256=SpeechPack.Hash(wav)}]};
        await File.WriteAllTextAsync(Path.Combine(names,"manifest.json"),JsonSerializer.Serialize(manifest),TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.SaveAsync("names-only",TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task FixedPackStartupAndSelectionAcceptEveryEntryInALargeManifest()
    {
        string directory = Path.Combine(root, "large");
        Directory.CreateDirectory(directory);
        var entries = new List<PackEntry>();
        for (int i = 0; i < 260; i++)
        {
            string text = new string('x', 940) + i;
            string file = SpeechPack.FileName(text);
            string path = Path.Combine(directory, file);
            PcmAudio.Write(path, [1, 0]);
            entries.Add(new(text, file, SpeechPack.Hash(File.ReadAllBytes(path))));
        }
        string manifest = Path.Combine(directory, "manifest.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new PackManifest(1, "m", "v", entries.ToArray())),
            TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(manifest).Length > 262144);
        Assert.Equal(entries.Count, PreGeneratedSpeech.Load(directory).Count);
        var imported = await new SpeechPackSettings(root).SaveAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(entries.Count, imported.Fixed.Count);
        Assert.Contains(entries[^1].Text, imported.Fixed.Keys);
    }

    [Fact]
    public void LocalGenerationUsesConfiguredModelVoiceAndThreadsWithoutEnablingPlayback()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, """{"enabled":false,"preparedSpeechOnly":true,"modelDirectory":"custom","voice":"zf_067","threads":6,"cacheDirectory":"existing-cache"}""");
        var options = SpeechOptions.LoadForGeneration(root);
        Assert.True(options.PreparedSpeechOnly);
        Assert.Equal(Path.Combine(root, "custom"), options.ModelDirectory);
        Assert.Equal(Path.Combine(root, "existing-cache"), options.CacheDirectory);
        Assert.Equal("zf_067", options.Voice);
        Assert.Equal(6, options.Threads);
        Assert.False(SpeechOptions.Load(root).Enabled);
    }
    [Fact]
    public async Task HotSwapPreservesQueuePlansAndNamesReferenceWithoutUsingOldInFlightResult()
    {
        string old=await Pack("old");string next=await Pack("new");await Pack(Path.Combine("new","CityNames"),"Paris");
        var service=new SpeechPackSettings(root);var workers=new List<ControlledSynth>();
        var resources=new ReloadableSpeech(new(){Enabled=true,PackDirectory=old,CacheDirectory=root},service,
            create:_=>{var synth=new ControlledSynth();workers.Add(synth);return synth;});
        await using var queue=new SpeechAnnouncementQueue(resources,_=>{},preGenerated:resources.Fixed);
        queue.UpdatePlan("navigation",["等待合成"]);
        await workers[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        var nameReference=resources.Names;
        await service.SaveAsync(next,TestContext.Current.CancellationToken);Assert.True(await resources.ApplyPendingAsync(queue));
        Assert.True(workers[0].Disposed);Assert.Same(nameReference,resources.Names);Assert.Contains("Paris",nameReference);
        workers[0].Finish.TrySetResult(Path.Combine(root,"stale.wav"));
        await workers[1].Started.Task.WaitAsync(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken);
        Assert.False(queue.IsReady("等待合成"));queue.ThrowIfFaulted();
        Assert.True(queue.IsReady("左转。"));Assert.Equal(next,service.Read().ActiveDirectory);
        workers[1].Finish.TrySetResult(Path.Combine(root,"new.wav"));
    }
    private sealed class ControlledSynth:ISpeechSynthesizer
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Finish {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Task<string> SynthesizeAsync(string text,CancellationToken token){Started.TrySetResult();return Finish.Task.WaitAsync(token);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    [Fact]
    public async Task DeveloperPageLinksBackToPlayerNavigationAndKeepsStatisticsExport()
    {
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async()=>
        {
            var component=typeof(NavPlugin).Assembly.GetTypes().Single(t=>t.Name=="Diagnostics"&&typeof(IComponent).IsAssignableFrom(t));
            var rendered=await renderer.RenderComponentAsync(component,ParameterView.Empty);
            string html=System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("返回导航主页",html);
            Assert.Contains("导出 JSON / CSV",html);
            Assert.DoesNotContain("speech-pack-directory",html);
            Assert.DoesNotContain("replay-comment",html);
        });
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
